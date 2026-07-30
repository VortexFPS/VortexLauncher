using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>Builds the game from a git checkout and stages the result in the same build store that
/// downloaded releases land in, so pin, update and rollback behave identically for a compiled build
/// and a downloaded one.
///
/// Every step streams as a job an operator can watch, because this takes long enough that a silent
/// pipeline is indistinguishable from a hung one.</summary>
public sealed partial class SourceProvider(LauncherPaths paths, BuildStore builds)
{
    public string WorkDir => Path.Combine(paths.Root, "source");
    public string ToolchainDir => Path.Combine(paths.Root, "toolchain");

    public sealed record Request
    {
        public string Repo { get; init; } = $"https://github.com/{LauncherConfig.Repo}.git";
        public string Ref { get; init; } = "main";

        /// <summary>Export preset to build. A runner builds only for its own platform: cross-OS Godot
        /// exports are unreliable (ADR-0014), and an export that silently produces a broken binary is
        /// worse than one that refuses.</summary>
        public string Target { get; init; } = "linux-dedicated";

        /// <summary>Reuse an existing data payload instead of downloading one. The usual answer on a
        /// box that already has an install.</summary>
        public string? LocalDataDir { get; init; }
    }

    public sealed record Result(bool Ok, string? BuildId, string? Error);

    public async Task<Result> BuildAsync(Request request, IProgress<string>? log,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(WorkDir);
        var checkout = Path.Combine(WorkDir, SafeSegment(request.Repo));

        try
        {
            var sha = await FetchAsync(request, checkout, log, ct);
            var godot = await EnsureToolchainAsync(checkout, log, ct);

            log?.Report("dotnet build");
            await RunAsync("dotnet", ["build", "-c", "Release"], checkout, log, ct);

            log?.Report($"godot export {request.Target}");
            await RunAsync(godot,
                ["--headless", "--path", checkout, "--export-release", request.Target,
                    Path.Combine(checkout, "dist", request.Target, "game")],
                checkout, log, ct);

            var buildId = $"source:{request.Ref}@{sha[..7]}";
            var staged = Stage(checkout, request, buildId, sha);
            log?.Report($"staged {buildId}");
            return new Result(true, staged, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new Result(false, null, ex.Message);
        }
    }

    private async Task<string> FetchAsync(Request request, string checkout, IProgress<string>? log,
        CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            log?.Report($"cloning {request.Repo}");
            // Blobless: the history is needed to check out a ref, the historical file contents are
            // not, and this repo's assets make a full clone many gigabytes.
            await RunAsync("git",
                ["clone", "--filter=blob:none", request.Repo, checkout], WorkDir, log, ct);
        }
        else
        {
            log?.Report("fetching");
            await RunAsync("git", ["fetch", "--all", "--prune"], checkout, log, ct);
        }

        await RunAsync("git", ["checkout", "--force", request.Ref], checkout, log, ct);
        // A detached tag or sha has nothing to pull, and that is not a failure.
        await RunAsync("git", ["pull", "--ff-only"], checkout, log, ct, allowFailure: true);

        var sha = (await CaptureAsync("git", ["rev-parse", "HEAD"], checkout, ct)).Trim();
        log?.Report($"at {sha[..7]}");
        return sha;
    }

    /// <summary>Make sure the pinned Godot console binary and export templates are cached.
    ///
    /// The version comes from the checkout's own docs/RUNNING.md pin, never a constant here. A
    /// launcher that hardcoded it would build the wrong engine the first time the game moved, and the
    /// symptom would be a successful build that crashes on load.</summary>
    private async Task<string> EnsureToolchainAsync(string checkout, IProgress<string>? log,
        CancellationToken ct)
    {
        var pin = ReadGodotPin(checkout)
            ?? throw new InvalidOperationException(
                "could not read the Godot version pin from docs/RUNNING.md; refusing to guess");

        var versionDir = Path.Combine(ToolchainDir, pin);

        if (FindGodotBinary(versionDir) is { } cached)
        {
            log?.Report($"toolchain {pin} cached");
            return cached;
        }

        // The engine is a custom build published on the game repo's own releases as engine-<pin>, not
        // a stock godotengine.org download. Fetching it from anywhere else would produce a build
        // against the wrong engine, which compiles and then misbehaves at runtime.
        log?.Report($"fetching Godot {pin}");
        await DownloadToolchainAsync(pin, versionDir, log, ct);

        return FindGodotBinary(versionDir)
               ?? throw new InvalidOperationException(
                   $"the engine-{pin} release did not contain a Godot binary for {PlatformKey.Current}. " +
                   "Install one into " + versionDir + " by hand, or fix the release. Version skew " +
                   "fails here deliberately rather than producing a build that looks fine and is not.");
    }

    /// <summary>Download and extract the pinned engine, plus its export templates.
    ///
    /// Both are cached per pinned version and shared across builds and branches: templates run about a
    /// gigabyte per ADR-0014, and re-downloading them for every branch switch would make source builds
    /// unusable on a metered connection.</summary>
    private async Task DownloadToolchainAsync(string pin, string versionDir, IProgress<string>? log,
        CancellationToken ct)
    {
        using var http = LauncherHttp.Create(TimeSpan.FromMinutes(10));
        var tag = "engine-" + pin;
        var url = $"https://api.github.com/repos/{LauncherConfig.Repo}/releases/tags/{tag}";

        var json = await http.GetStringAsync(url, ct);
        var release = System.Text.Json.JsonSerializer.Deserialize<GitHubApiFeed.ApiRelease>(
                          json, ReleaseManifest.JsonOptions)
                      ?? throw new InvalidOperationException($"release {tag} not found");

        var editor = PickAsset(release, EditorAssetHints());
        var templates = PickAsset(release, ["export_templates", "export-templates", ".tpz"]);

        if (editor is null)
            throw new InvalidOperationException(
                $"release {tag} has no editor asset for {PlatformKey.Current}; " +
                $"it carries: {string.Join(", ", release.Assets.Select(a => a.Name))}");

        Directory.CreateDirectory(versionDir);
        var downloader = new DownloadService(http);

        await FetchAndExtractAsync(downloader, editor, versionDir, log, ct);

        if (templates is not null)
            await FetchAndExtractAsync(downloader, templates,
                Path.Combine(versionDir, "templates"), log, ct);
        else
            log?.Report(
                $"warning: {tag} carries no export templates; a client export will fail, though a " +
                "headless dedicated build may not need them");
    }

    private async Task FetchAndExtractAsync(DownloadService downloader,
        GitHubApiFeed.ApiAsset asset, string destination, IProgress<string>? log, CancellationToken ct)
    {
        var staging = Path.Combine(ToolchainDir, "staging");
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, asset.Name);

        // The digest is what makes this safe to cache and reuse. An asset with none is refused rather
        // than trusted, on the same rule the game installer already follows.
        var sha = asset.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true
            ? asset.Digest["sha256:".Length..].ToLowerInvariant()
            : throw new InvalidOperationException(
                $"{asset.Name} has no published sha256; refusing an unverifiable toolchain download");

        log?.Report($"downloading {asset.Name} ({asset.Size / (1 << 20)} MB)");
        await downloader.DownloadAsync(asset.BrowserDownloadUrl, archive, asset.Size, sha,
            new Progress<double>(f => log?.Report($"  {f:P0}")), ct);

        log?.Report($"extracting {asset.Name}");
        Directory.CreateDirectory(destination);
        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(
            archive, destination, overwriteFiles: true), ct);

        File.Delete(archive);

        if (!OperatingSystem.IsWindows())
            MarkExecutables(destination);
    }

    /// <summary>Zip extraction does not restore the +x bit, so a freshly extracted Godot will not
    /// run on Linux or macOS without this.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // callers guard on IsWindows()
    private static void MarkExecutables(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.Contains("godot", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.SetUnixFileMode(file, File.GetUnixFileMode(file)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }
    }

    private static GitHubApiFeed.ApiAsset? PickAsset(
        GitHubApiFeed.ApiRelease release, IReadOnlyList<string> hints) =>
        release.Assets.FirstOrDefault(a =>
            hints.All(h => a.Name.Contains(h, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Substrings every candidate asset name must contain for this platform. The console
    /// build is wanted on Windows: the plain one detaches from the terminal and the build output goes
    /// nowhere.</summary>
    private static string[] EditorAssetHints() =>
        OperatingSystem.IsWindows() ? ["win", "console"]
        : OperatingSystem.IsMacOS() ? ["macos"]
        : ["linux"];

    private static string? FindGodotBinary(string versionDir)
    {
        if (!Directory.Exists(versionDir))
            return null;

        return Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("godot", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("templates", StringComparison.OrdinalIgnoreCase))
            .Where(f => OperatingSystem.IsWindows()
                ? f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                : Path.GetExtension(f) is "" or ".x86_64" or ".arm64")
            .OrderByDescending(f => f.Contains("console", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>Read the pinned engine version out of the checkout's own docs.</summary>
    public static string? ReadGodotPin(string checkout)
    {
        var path = Path.Combine(checkout, "docs", "RUNNING.md");
        if (!File.Exists(path))
            return null;

        var match = GodotPinPattern().Match(File.ReadAllText(path));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"godot[- ]?(\d+\.\d+\.\d+[-\w.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GodotPinPattern();

    private string Stage(string checkout, Request request, string buildId, string sha)
    {
        var dirName = BuildRecord.SafeDirName(buildId);
        var destination = Path.Combine(paths.VersionsDir, dirName);
        var exported = Path.Combine(checkout, "dist", request.Target);

        if (!Directory.Exists(exported))
            throw new InvalidOperationException(
                $"the export produced nothing at {exported}; check the preset name");

        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        var root = request.Target;
        CopyTree(exported, Path.Combine(destination, root));

        builds.Register(new BuildRecord
        {
            Id = buildId,
            DirName = dirName,
            Version = sha[..7],
            PlatformKey = PlatformKey.Current,
            Layout = request.LocalDataDir is null
                ? InstalledState.LayoutFat
                : InstalledState.LayoutCore,
            Root = root,
            Provider = BuildProviders.Source,
            InstalledAt = DateTimeOffset.UtcNow,
        });

        return buildId;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    private static async Task RunAsync(string exe, string[] args, string workingDir,
        IProgress<string>? log = null, CancellationToken ct = default, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException(
                $"{exe} {string.Join(' ', args)} exited with {process.ExitCode}");
    }

    private static async Task<string> CaptureAsync(string exe, string[] args, string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    private static string SafeSegment(string repo)
    {
        var name = repo.TrimEnd('/').Split('/').LastOrDefault() ?? "repo";
        if (name.EndsWith(".git", StringComparison.Ordinal))
            name = name[..^4];
        return BuildRecord.SafeDirName(name);
    }
}
