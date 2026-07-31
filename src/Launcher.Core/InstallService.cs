using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>What's installed right now — persisted as game/current.json. <see cref="Root"/> is
/// the zip's internal top dir (the game dir is versions/&lt;<see cref="Dir"/>&gt;/&lt;Root&gt;); a
/// "core" <see cref="Layout"/> install launches with --data pointing into the shared asset store.
///
/// <see cref="BuildId"/> and <see cref="DirName"/> are null in every marker written before source
/// builds existed, and null means "the same as Version" because that is what a release build's id and
/// directory name are. They exist because a source build breaks that identity: its id is
/// `source:&lt;preset&gt;:&lt;ref&gt;@&lt;sha7&gt;`, its directory is a flattened form of that, and its
/// version is the sha alone. A marker that recorded only the version pointed at versions/&lt;sha7&gt;,
/// which does not exist, so pinning a source build wrote a file that resolved to nothing and the
/// launcher reported nothing installed.</summary>
public sealed record InstalledState(
    string Version, string Layout, string PlatformKey, string Root, string? AssetsVersion,
    string? BuildId = null, string? DirName = null)
{
    public const string LayoutFat = "fat";
    public const string LayoutCore = "core";

    // Both are derived views, not stored facts. Serializing them would put four fields in current.json
    // where two are authoritative, and the next reader would have to work out which pair to trust.

    /// <summary>The build-store id this marker pins, for comparing against <c>BuildRecord.Id</c>.</summary>
    [JsonIgnore]
    public string Id => BuildId ?? Version;

    /// <summary>The directory under versions/ this marker points at.</summary>
    [JsonIgnore]
    public string Dir => DirName ?? Version;
}

/// <summary>Game-install lifecycle (ADR-0015 §3/§6): download → verify → extract to staging →
/// move into versions/ → flip current.json. Previous version is retained for rollback.</summary>
public sealed class InstallService(LauncherPaths paths, IDownloader downloader,
    BuildStore? builds = null, IArchiveExtractor? extractor = null)
{
    /// <summary>Side-by-side builds, pin and GC. Defaulted rather than required so the existing
    /// two-argument construction keeps working.</summary>
    public BuildStore Builds { get; } = builds ?? new BuildStore(paths);

    /// <summary>Managed on Windows/Linux, ditto on macOS — the macOS package is an .app bundle whose
    /// symlinks the managed extractor silently drops. Injectable, like the downloader, so the macOS
    /// decision is testable on a machine that isn't a Mac.</summary>
    private readonly IArchiveExtractor _extractor = extractor ?? ArchiveExtractor.ForCurrentPlatform();

    public InstalledState? LoadCurrent()
    {
        try
        {
            if (!File.Exists(paths.CurrentJsonPath))
                return null;
            var state = JsonSerializer.Deserialize<InstalledState>(
                File.ReadAllText(paths.CurrentJsonPath), ReleaseManifest.JsonOptions);
            // Trust the marker only if the install it points at is actually on disk.
            return state is not null && Directory.Exists(GameDirOf(state)) ? state : null;
        }
        catch (JsonException)
        {
            return null; // corrupt marker = not installed; the next install rewrites it
        }
    }

    public string GameDirOf(InstalledState s) => Path.Combine(paths.VersionsDir, s.Dir, s.Root);

    public string? AssetsDataDirOf(InstalledState s) => s.AssetsVersion is null
        ? null
        : Path.Combine(paths.AssetStoreDir, s.AssetsVersion, "assets", "data");

    /// <summary>Download, verify, extract and flip — the whole thing, for callers that have already
    /// decided (the CLI, and the UI's fully-automatic mode).</summary>
    public async Task<InstalledState> InstallAsync(ReleaseManifest manifest, string platformKey,
        bool preferCore, IProgress<(string Phase, double Fraction)>? progress, CancellationToken ct) =>
        Apply(await StageAsync(manifest, platformKey, preferCore, progress, ct));

    /// <summary>Everything except going live: the new build ends up in versions/ beside the old one
    /// and current.json still points at what the player has been playing.
    ///
    /// The split is what makes "download it now, switch when you say so" possible, and it is free
    /// because the install was already built this way — the ADR-0015 invariant is verify-before-swap,
    /// so all the expensive, failure-prone work (download, checksum, extract) already happened
    /// out-of-tree with one <c>Directory.Move</c> between it and live. Staging just stops before the
    /// three cheap lines that flip the marker.
    ///
    /// A staged build that is never applied is not lost disk: <c>BuildStore.List()</c> adopts
    /// directories under versions/ with no entry in builds.json, so it shows up in
    /// <c>vortex builds list</c> and is collectable like any other.</summary>
    public async Task<InstalledState> StageAsync(ReleaseManifest manifest, string platformKey,
        bool preferCore, IProgress<(string Phase, double Fraction)>? progress, CancellationToken ct)
    {
        var plat = manifest.PlatformFor(platformKey)
            ?? throw new InvalidOperationException(
                $"release {manifest.Tag} has no {platformKey} package (its build job may have failed)");

        // Core needs the assets pack in the manifest; otherwise fall back to fat.
        ManifestFile file;
        string layout;
        if (preferCore && plat.Core is not null && manifest.Assets is not null)
            (file, layout) = (plat.Core, InstalledState.LayoutCore);
        else if (plat.Fat is not null)
            (file, layout) = (plat.Fat, InstalledState.LayoutFat);
        else if (plat.Core is not null && manifest.Assets is not null)
            (file, layout) = (plat.Core, InstalledState.LayoutCore);
        else
            throw new InvalidOperationException($"release {manifest.Tag} has no usable {platformKey} package");

        paths.EnsureCreated();

        string? assetsVersion = null;
        if (layout == InstalledState.LayoutCore)
        {
            assetsVersion = manifest.Assets!.Version;
            await EnsureAssetsAsync(manifest.Assets, progress, ct);
        }

        var zipPath = Path.Combine(paths.StagingDir, file.Name);
        await downloader.DownloadAsync(file.Url, zipPath, file.Size, file.Sha256,
            new Progress<double>(f => progress?.Report(("Downloading", f))), ct);

        progress?.Report(("Extracting", 0));
        var extractDir = Path.Combine(paths.StagingDir, "extract-" + manifest.Version);
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        await _extractor.ExtractAsync(zipPath, extractDir, ct);

        var root = file.Root ?? FindSingleRootDir(extractDir);
        if (!Directory.Exists(Path.Combine(extractDir, root)))
            throw new InvalidOperationException(
                $"{file.Name} did not contain the expected '{root}/' top-level directory");

        // The swap: everything above verified out-of-tree; one Move flips it live.
        progress?.Report(("Installing", 0));
        var versionDir = Path.Combine(paths.VersionsDir, manifest.Version);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true); // explicit reinstall of this version
        Directory.Move(extractDir, versionDir);
        File.Delete(zipPath);

        return new InstalledState(manifest.Version, layout, platformKey, root, assetsVersion);
    }

    /// <summary>Make a staged build the one that launches. This is the only step in an install that
    /// changes what pressing Play does, and it is three file operations — which is why it is safe to
    /// hold back until the player agrees to it.
    ///
    /// Registering with the build store happens here rather than at stage time so that GC's
    /// <c>protectedId</c> and the store's idea of the current build change together; a build the
    /// player declined to switch to is still adopted by <c>BuildStore.List()</c> from its
    /// directory.</summary>
    public InstalledState Apply(InstalledState staged)
    {
        SaveCurrent(staged);
        Builds.Register(BuildRecord.ForRelease(staged, DateTimeOffset.UtcNow));
        Builds.Gc(protectedId: staged.Version);
        return staged;
    }

    /// <summary>Whether a staged build is still on disk, for a launcher that staged it in an earlier
    /// session and wants to offer the swap without re-downloading.</summary>
    public bool IsStaged(InstalledState staged) => Directory.Exists(GameDirOf(staged));

    /// <summary>Ensure the content-addressed asset pack is in the shared store (core layout).
    /// Store hit = zero bytes downloaded — the whole point of the split payload (ADR-0015 §4).</summary>
    private async Task EnsureAssetsAsync(ManifestAssets assets,
        IProgress<(string Phase, double Fraction)>? progress, CancellationToken ct)
    {
        var storeDir = Path.Combine(paths.AssetStoreDir, assets.Version);
        if (Directory.Exists(Path.Combine(storeDir, "assets", "data")))
            return;

        var zipPath = Path.Combine(paths.StagingDir, assets.Name);
        await downloader.DownloadAsync(assets.Url, zipPath, assets.Size, assets.Sha256,
            new Progress<double>(f => progress?.Report(("Downloading game data", f))), ct);

        progress?.Report(("Extracting game data", 0));
        var tmp = storeDir + ".staging";
        if (Directory.Exists(tmp))
            Directory.Delete(tmp, recursive: true);
        await _extractor.ExtractAsync(zipPath, tmp, ct);
        Directory.CreateDirectory(paths.AssetStoreDir);
        if (Directory.Exists(storeDir))
            Directory.Delete(storeDir, recursive: true);
        Directory.Move(tmp, storeDir);
        File.Delete(zipPath);
    }

    /// <summary>Point current.json at an already-installed build without downloading anything. This is
    /// rollback: the previous build is still on disk precisely so that this is a file write and not a
    /// reinstall.</summary>
    public InstalledState Pin(BuildRecord build)
    {
        if (!Directory.Exists(Builds.GameDirOf(build)))
            throw new InvalidOperationException(
                $"build {build.Id} is recorded but its directory is gone; reinstall it");

        // Id and DirName are carried across rather than derived from Version, because for a source
        // build the three are different strings and only the version is ambiguous: two presets built
        // from one sha share it.
        var state = new InstalledState(
            build.Version, build.Layout, build.PlatformKey, build.Root, build.AssetsVersion,
            build.Id, build.DirName);
        SaveCurrent(state);
        return state;
    }

    private void SaveCurrent(InstalledState state)
    {
        // temp + move so a crash mid-write can't leave a torn current.json
        var tmp = paths.CurrentJsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, ReleaseManifest.JsonOptions));
        File.Move(tmp, paths.CurrentJsonPath, overwrite: true);
    }

    private static string FindSingleRootDir(string extractDir)
    {
        var entries = Directory.GetFileSystemEntries(extractDir);
        return entries.Length == 1 && Directory.Exists(entries[0])
            ? Path.GetFileName(entries[0])!
            : throw new InvalidOperationException(
                "zip has no single top-level directory and the manifest carried no 'root'");
    }
}
