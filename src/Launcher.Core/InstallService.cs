using System.Text.Json;

namespace Launcher.Core;

/// <summary>What's installed right now — persisted as game/current.json. <see cref="Root"/> is
/// the zip's internal top dir (the game dir is versions/&lt;Version&gt;/&lt;Root&gt;); a "core"
/// <see cref="Layout"/> install launches with --data pointing into the shared asset store.</summary>
public sealed record InstalledState(
    string Version, string Layout, string PlatformKey, string Root, string? AssetsVersion)
{
    public const string LayoutFat = "fat";
    public const string LayoutCore = "core";
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

    public string GameDirOf(InstalledState s) => Path.Combine(paths.VersionsDir, s.Version, s.Root);

    public string? AssetsDataDirOf(InstalledState s) => s.AssetsVersion is null
        ? null
        : Path.Combine(paths.AssetStoreDir, s.AssetsVersion, "assets", "data");

    public async Task<InstalledState> InstallAsync(ReleaseManifest manifest, string platformKey,
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

        var state = new InstalledState(manifest.Version, layout, platformKey, root, assetsVersion);
        SaveCurrent(state);
        Builds.Register(BuildRecord.ForRelease(state, DateTimeOffset.UtcNow));
        Builds.Gc(protectedId: state.Version);
        return state;
    }

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

        var state = new InstalledState(
            build.Version, build.Layout, build.PlatformKey, build.Root, build.AssetsVersion);
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
