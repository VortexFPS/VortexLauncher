using System.ComponentModel;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace Launcher.Core;

/// <summary>Failure codes a source build can report. They are part of the CLI's contract: `vortex
/// source build --json` puts one in the envelope, and the exit code is derived from it, so a script
/// can tell "install Godot" from "this ref does not build".</summary>
public static class SourceFailure
{
    public const string GitMissing = "git_missing";
    public const string DotnetMissing = "dotnet_missing";
    public const string PythonMissing = "python_missing";
    public const string BashMissing = "bash_missing";
    public const string EditorMissing = "editor_missing";
    public const string EditorUnusable = "editor_unusable";

    /// <summary>The editor is not the engine the checkout pins. Always names both versions.</summary>
    public const string EngineSkew = "engine_skew";

    public const string CheckoutIncomplete = "checkout_incomplete";
    public const string LockfileUnreadable = "lockfile_unreadable";

    /// <summary>No such export preset, or one this box cannot build.</summary>
    public const string PresetUnknown = "preset_unknown";
    public const string CrossPlatform = "cross_platform";

    public const string GitFailed = "git_failed";
    public const string TemplateFetchFailed = "template_fetch_failed";
    public const string ExportFailed = "export_failed";
    public const string PackageFailed = "package_failed";

    /// <summary>tools/verify-engine-template.py refused the build. Either the preset is not configured
    /// to use the pinned template, or the exported binary does not carry it.</summary>
    public const string VerificationFailed = "verification_failed";

    public const string StepFailed = "step_failed";
}

public sealed class SourceBuildException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Builds the game from a git checkout and stages the result in the same build store that
/// downloaded releases land in, so pin, update and rollback behave identically for a compiled build
/// and a downloaded one.
///
/// The pipeline is deliberately the release workflow's pipeline, step for step:
///
///   fetch -> read the engine pin -> resolve and version-check the editor -> import ->
///   fetch the pinned TEMPLATE -> verify the preset points at it -> dotnet build -> export ->
///   verify the exported binary -> fetch maps -> package -> stage
///
/// Two of those steps are the whole reason this class is not shorter. The launcher fetches the
/// template through the checkout's own tools/data/fetch-engine-template.py and verifies the result
/// through its own tools/verify-engine-template.py, rather than reimplementing either. A second
/// downloader reading the same lockfile is how a project ends up patched in CI and stock locally, and
/// the verify step is the only assertion that speaks to what actually shipped: measured in the game
/// repo, an empty custom_template/release makes Godot export a complete, launchable binary from the
/// STOCK engine without failing. CI closed that trap; a source build that skipped these two would
/// reopen it on every operator's box.
///
/// Every step streams as a job an operator can watch, because this takes long enough that a silent
/// pipeline is indistinguishable from a hung one.</summary>
public sealed class SourceProvider(LauncherPaths paths, BuildStore builds)
{
    public string WorkDir => Path.Combine(paths.Root, "source");

    /// <summary>Checkouts are keyed on the SOURCE NAME, not the repo's basename. Two sources pointing
    /// at different forks of VortexArena would otherwise share one working tree and thrash it back and
    /// forth on every build, which reads as a mysteriously slow clone.</summary>
    public string CheckoutFor(string name) =>
        Path.Combine(WorkDir, "checkouts", SourceStore.ValidateName(name));

    /// <summary>Delete a checkout and everything under it.
    ///
    /// Read-only attributes are cleared first because git marks the loose objects under .git/objects
    /// read-only, and Directory.Delete refuses those on Windows. Without this pass, deleting a
    /// checkout fails on every checkout there is, which is the only kind this method is ever given.</summary>
    public void DeleteCheckout(string name)
    {
        var dir = CheckoutFor(name);
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        Directory.Delete(dir, recursive: true);
    }

    public sealed record Result
    {
        public required bool Ok { get; init; }
        public string? BuildId { get; init; }
        public string? Code { get; init; }
        public string? Error { get; init; }
        public string? Sha { get; init; }
        public string? Preset { get; init; }
        public string? PlatformKey { get; init; }
        public string? EngineVersion { get; init; }
        public string? EngineTag { get; init; }
        public string? EditorPath { get; init; }
        public string? EditorVersion { get; init; }
        public string? Dir { get; init; }
    }

    /// <summary>One prerequisite, as `source status` reports it.</summary>
    public sealed record ToolReport(string Name, bool Ok, string? Path, string? Problem);

    public sealed record Preflight
    {
        public required string Name { get; init; }
        public required string Repo { get; init; }
        public required string Ref { get; init; }
        public required string Checkout { get; init; }
        public bool CheckedOut { get; init; }
        public string? Sha { get; init; }
        public required string Preset { get; init; }
        public string? PlatformKey { get; init; }
        public string? EngineVersion { get; init; }
        public string? EngineTag { get; init; }
        public string? TemplateFile { get; init; }
        public bool TemplatePresent { get; init; }
        public required IReadOnlyList<ToolReport> Tools { get; init; }
        public required IReadOnlyList<string> Problems { get; init; }
        public bool Ready => Problems.Count == 0;
        public string? LastBuildId { get; init; }
        public DateTimeOffset? LastBuiltAt { get; init; }
    }

    /// <summary>The preset a build targets when the operator names none.
    ///
    /// Linux defaults to the dedicated server rather than the client: the CLI is what a host operator
    /// installs on a headless box, and that box wants a server. `--target linux-client` is one flag
    /// away for the other case.</summary>
    public static string DefaultPreset() =>
        OperatingSystem.IsWindows() ? "windows-client"
        : OperatingSystem.IsMacOS() ? "macos-client"
        : "linux-dedicated";

    /// <summary>Export preset to manifest platform key, inverted from the table the release feed
    /// already uses so a preset cannot come to mean two different things in one process.</summary>
    public static string? PlatformKeyForPreset(string preset)
    {
        foreach (var (key, root) in PlatformKey.ZipSuffixMap.Values)
            if (string.Equals(root, preset, StringComparison.Ordinal))
                return key;
        return null;
    }

    public async Task<Result> BuildAsync(SourceSpec spec, bool fetchMaps, IProgress<string>? log,
        CancellationToken ct = default)
    {
        try
        {
            var checkout = CheckoutFor(spec.Name);
            var git = BuildTools.RequireOnPath("git", SourceFailure.GitMissing,
                "Install git from https://git-scm.com/downloads.");
            var dotnet = BuildTools.RequireOnPath("dotnet", SourceFailure.DotnetMissing,
                "Install the .NET SDK from https://dotnet.microsoft.com/download.");

            var sha = await FetchAsync(git, spec, checkout, log, ct);

            var pin = EnginePin.Read(checkout);
            var preset = spec.Target ?? DefaultPreset();
            var template = ResolveTemplate(pin, preset, checkout);
            var platformKey = PlatformKeyForPreset(preset)!;

            var editor = GodotEditor.Resolve(spec.GodotPath);
            editor.RequireMatches(pin, GameCheckout.EngineLockPath(checkout));
            log?.Report($"godot {editor.RawVersion} at {editor.Path}");

            var python = BuildTools.ResolvePython();
            var bash = BuildTools.ResolveBash();

            await RepairNuGetSourcesAsync(checkout, dotnet, log, ct);

            // Godot writes its import cache on first open. Non-fatal on purpose: a headless import
            // reports missing-dependency warnings as a non-zero exit on a tree that exports fine, and
            // the release workflow makes the same call for the same reason.
            log?.Report("importing resources");
            await RunAsync(editor.Path, ["--headless", "--path", checkout, "--import"],
                checkout, log, ct, allowFailure: true);

            log?.Report($"fetching the pinned {template.Platform} export template " +
                        $"({pin.TemplateTag ?? "untagged"})");
            await RunAsync(python,
                [Script(checkout, GameCheckout.FetchTemplateScript(checkout),
                        "tools/data/fetch-engine-template.py"),
                    "--only", template.Platform],
                checkout, log, ct, failureCode: SourceFailure.TemplateFetchFailed);

            // Before the export, not after: this catches an emptied custom_template/release in
            // seconds, and that is G10's actual cause. Godot does not fail on an empty field, it
            // silently falls back to the stock template.
            log?.Report($"verifying {preset} is configured to use the pinned template");
            await RunAsync(python,
                [Script(checkout, GameCheckout.VerifyTemplateScript(checkout),
                        "tools/verify-engine-template.py"),
                    "--preset-config", preset],
                checkout, log, ct, failureCode: SourceFailure.VerificationFailed);

            // The export builds the C# project itself, so this is not strictly required. It earns its
            // place by failing first: a compile error here is a compiler diagnostic, and the same
            // error inside an export is buried in a Godot log that reports it as a failed export.
            log?.Report("dotnet build");
            var project = GameCheckout.GameProject(checkout);
            string[] buildArgs = project is null
                ? ["build", "-c", "Release"]
                : ["build", Relative(checkout, project), "-c", "Release"];
            await RunAsync(dotnet, buildArgs, checkout, log, ct);

            var exportPath = ExportPathFor(checkout, preset);
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);

            log?.Report($"exporting {preset}");
            // Godot's headless export exits non-zero on benign warnings, so the assertion is that the
            // output appeared rather than the exit code. release.yml does exactly this.
            await RunAsync(editor.Path,
                ["--headless", "--path", checkout, "--export-release", preset, exportPath],
                checkout, log, ct, allowFailure: true);

            if (!File.Exists(exportPath) && !Directory.Exists(exportPath))
                throw new SourceBuildException(SourceFailure.ExportFailed,
                    $"the export produced nothing at {exportPath}. Godot's headless export exits " +
                    "non-zero on warnings, so the real failure is above this line in the streamed log.");

            // The only check that speaks to what shipped. --patches re-hashes the patch files too, so
            // a silently edited patch fails here as well.
            log?.Report("verifying the exported binary carries the pinned engine");
            await RunAsync(python,
                [Script(checkout, GameCheckout.VerifyTemplateScript(checkout),
                        "tools/verify-engine-template.py"), "--patches",
                    "--binary", Relative(checkout, exportPath), "--preset", preset],
                checkout, log, ct, failureCode: SourceFailure.VerificationFailed);

            if (fetchMaps)
            {
                log?.Report("fetching compiled maps");
                await RunAsync(python,
                    [Script(checkout, GameCheckout.FetchMapsScript(checkout),
                        "tools/data/fetch-maps.py")],
                    checkout, log, ct);
            }

            // package.sh lays content, licences and the launch script beside the binary, which is what
            // turns an export into something that runs. Called rather than reimplemented for the same
            // reason as the template fetch, and with a sharper edge: macOS puts data INSIDE the
            // bundle, and a launcher-local copy of that rule would be wrong on exactly one platform.
            //
            // Its exit code is not the assertion, for a specific reason: the script's last statement
            // is `$do_zip && info ...`, so under --no-zip it returns 1 having done everything right.
            // Nothing noticed because CI always zips. So this asserts on the output the way the
            // release workflow asserts on the export's, which is the stronger check anyway.
            log?.Report("packaging");
            await RunAsync(bash,
                [Script(checkout, GameCheckout.PackageScript(checkout), "tools/package.sh"),
                    "--no-zip", "--version", sha[..7], preset],
                checkout, log, ct, allowFailure: true);

            RequirePackaged(checkout, preset, exportPath);

            var buildId = BuildIdFor(spec.Ref, preset, sha);
            var dir = Stage(checkout, preset, buildId, sha, platformKey);
            log?.Report($"staged {buildId}");

            return new Result
            {
                Ok = true,
                BuildId = buildId,
                Sha = sha,
                Preset = preset,
                PlatformKey = platformKey,
                EngineVersion = pin.Version,
                EngineTag = pin.TemplateTag,
                EditorPath = editor.Path,
                EditorVersion = editor.RawVersion,
                Dir = dir,
            };
        }
        catch (SourceBuildException ex)
        {
            return new Result { Ok = false, Code = ex.Code, Error = ex.Message };
        }
        catch (IOException ex)
        {
            return new Result { Ok = false, Code = SourceFailure.StepFailed, Error = ex.Message };
        }
    }

    /// <summary>Answer "would a build work here, and against which engine", without building.
    ///
    /// Collects every problem rather than stopping at the first, because the answer an operator wants
    /// is the whole list of things to install, not one of them per five-minute round trip.</summary>
    public Preflight Inspect(SourceSpec spec)
    {
        var checkout = CheckoutFor(spec.Name);
        var problems = new List<string>();
        var tools = new List<ToolReport>();

        foreach (var (name, resolve) in new (string, Func<string>)[]
                 {
                     ("git", () => BuildTools.RequireOnPath("git", SourceFailure.GitMissing,
                         "Install git from https://git-scm.com/downloads.")),
                     ("dotnet", () => BuildTools.RequireOnPath("dotnet", SourceFailure.DotnetMissing,
                         "Install the .NET SDK from https://dotnet.microsoft.com/download.")),
                     ("python", BuildTools.ResolvePython),
                     ("bash", BuildTools.ResolveBash),
                 })
        {
            try
            {
                tools.Add(new ToolReport(name, true, resolve(), null));
            }
            catch (SourceBuildException ex)
            {
                tools.Add(new ToolReport(name, false, null, ex.Message));
                problems.Add(ex.Message);
            }
        }

        var preset = spec.Target ?? DefaultPreset();
        var checkedOut = Directory.Exists(Path.Combine(checkout, ".git"));

        if (!checkedOut)
        {
            // Everything below is read out of the checkout, so with none there the honest answer is
            // "unknown", not "fine". Reporting a clean bill here would be the same mistake
            // verify-engine-template.py refuses to make.
            problems.Add($"no checkout at {checkout}; `vortex source build {spec.Name}` clones it, and " +
                         "the engine pin cannot be read until it exists");
            return new Preflight
            {
                Name = spec.Name, Repo = spec.Repo, Ref = spec.Ref, Checkout = checkout,
                CheckedOut = false, Preset = preset, PlatformKey = PlatformKeyForPreset(preset),
                Tools = tools, Problems = problems,
                LastBuildId = spec.LastBuildId, LastBuiltAt = spec.LastBuiltAt,
            };
        }

        var sha = ReadHeadSha(BuildTools.FindOnPath("git") ?? "git", checkout);

        if (sha is null)
            // There is a .git here and git still cannot name HEAD, which is what an interrupted clone
            // leaves behind. Reporting ready would send the operator into a build that dies at the
            // first `git fetch`.
            problems.Add($"{checkout} has a .git directory but git cannot read HEAD there, so the " +
                         $"clone is incomplete. `vortex source remove {spec.Name} --purge` deletes it " +
                         "and the next build re-clones.");

        EnginePin? pin = null;
        TemplatePin? template = null;
        try
        {
            pin = EnginePin.Read(checkout);
            template = ResolveTemplate(pin, preset, checkout);
        }
        catch (SourceBuildException ex)
        {
            problems.Add(ex.Message);
        }

        if (pin is not null)
        {
            try
            {
                var editor = GodotEditor.Resolve(spec.GodotPath);
                editor.RequireMatches(pin, GameCheckout.EngineLockPath(checkout));
                tools.Add(new ToolReport("godot", true, $"{editor.Path} ({editor.RawVersion})", null));
            }
            catch (SourceBuildException ex)
            {
                tools.Add(new ToolReport("godot", false, null, ex.Message));
                problems.Add(ex.Message);
            }
        }

        // Present is not the same as verified: the build re-checks the sha256 through
        // fetch-engine-template.py, which is the tool that owns that answer. Size is the cheap tell.
        var templatePath = template is null
            ? null
            : Path.Combine(GameCheckout.TemplateDir(checkout), template.FileName);
        var templatePresent = templatePath is not null && File.Exists(templatePath) &&
                              (template!.Bytes == 0 || new FileInfo(templatePath).Length == template.Bytes);

        return new Preflight
        {
            Name = spec.Name,
            Repo = spec.Repo,
            Ref = spec.Ref,
            Checkout = checkout,
            CheckedOut = true,
            Sha = sha,
            Preset = preset,
            PlatformKey = PlatformKeyForPreset(preset),
            EngineVersion = pin?.Version,
            EngineTag = pin?.TemplateTag,
            TemplateFile = template?.FileName,
            TemplatePresent = templatePresent,
            Tools = tools,
            Problems = problems,
            LastBuildId = spec.LastBuildId,
            LastBuiltAt = spec.LastBuiltAt,
        };
    }

    /// <summary>The build store's id for a source build.
    ///
    /// The plan writes this as `source:{ref}@{sha}`; the preset is in it because four presets now
    /// exist and two of them build on the same OS. Without it, building linux-client and then
    /// linux-dedicated from one ref would have the second silently replace the first under an id that
    /// no longer describes it.</summary>
    public static string BuildIdFor(string reference, string preset, string sha) =>
        $"source:{preset}:{reference}@{sha[..Math.Min(7, sha.Length)]}";

    private static TemplatePin ResolveTemplate(EnginePin pin, string preset, string checkout)
    {
        var template = pin.TemplateForPreset(preset);

        if (template is null)
        {
            var known = ExportPresets.Read(checkout).Keys.OrderBy(k => k, StringComparer.Ordinal);
            throw new SourceBuildException(SourceFailure.PresetUnknown,
                $"{GameCheckout.EngineLockPath(checkout)} pins no engine template for preset " +
                $"'{preset}'. Pinned presets: {string.Join(", ", pin.KnownPresets)}. " +
                $"export_presets.cfg defines: {string.Join(", ", known)}. A preset with no pinned " +
                "template would export against whatever stock template this box happens to have, so " +
                "the launcher refuses rather than guessing.");
        }

        // ADR-0014: cross-OS Godot exports are unreliable, and one that silently produces a broken
        // binary is worse than one that refuses. The lockfile already names the platform, so this
        // costs nothing to check and saves a 20-minute export that could not have worked.
        var here = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : "linux";

        if (!string.Equals(template.Platform, here, StringComparison.Ordinal))
            throw new SourceBuildException(SourceFailure.CrossPlatform,
                $"preset '{preset}' builds for {template.Platform} and this box is {here}. A runner " +
                "builds only for its own platform (ADR-0014): cross-OS Godot exports are unreliable " +
                "and fail by producing a broken binary rather than by refusing. Build this preset on " +
                $"a {template.Platform} box.");

        if (PlatformKeyForPreset(preset) is null)
            throw new SourceBuildException(SourceFailure.PresetUnknown,
                $"preset '{preset}' has no manifest platform key in PlatformKey.ZipSuffixMap, so a " +
                "build from it could not be staged where an instance would find it. Add it there " +
                "alongside the release-artifact naming it already describes.");

        return template;
    }

    /// <summary>Assert the export directory is a layout a player could run, rather than a bare binary.
    ///
    /// The content directory is derived from the artifact's own shape rather than from the preset
    /// name: a .app is a bundle and its data goes inside it, anything else gets data beside it. Keying
    /// on "macos-client" would be a second copy of package.sh's rule, and the copy is what goes stale.
    /// A staged build missing its content starts and then finds no maps, which reads as a broken game
    /// rather than an incomplete build.</summary>
    private static void RequirePackaged(string checkout, string preset, string exportPath)
    {
        var bundle = exportPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
        var content = bundle
            ? Path.Combine(exportPath, "Contents", "Resources", "data")
            : Path.Combine(GameCheckout.DistDir(checkout, preset), "data");

        if (!File.Exists(exportPath) && !Directory.Exists(exportPath))
            throw new SourceBuildException(SourceFailure.PackageFailed,
                $"packaging removed or never produced {exportPath}");

        if (!Directory.Exists(content) || !Directory.EnumerateFileSystemEntries(content).Any())
            throw new SourceBuildException(SourceFailure.PackageFailed,
                $"tools/package.sh left no game content at {content}, so this build would start and " +
                "find nothing to load. Its output is above; the usual cause is an empty data/ in the " +
                "checkout, which `python tools/data/fetch-maps.py` fills for maps and which is " +
                "otherwise committed.");
    }

    private static string ExportPathFor(string checkout, string preset)
    {
        var presets = ExportPresets.Read(checkout);
        if (!presets.TryGetValue(preset, out var configured) || configured.Length == 0)
            throw new SourceBuildException(SourceFailure.PresetUnknown,
                $"export_presets.cfg has no preset '{preset}' with an export_path. Defined: " +
                $"{string.Join(", ", presets.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

        var relative = configured.StartsWith("res://", StringComparison.Ordinal)
            ? configured["res://".Length..]
            : configured;

        return Path.GetFullPath(Path.Combine(checkout, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>HEAD's commit, or null if git could not name one.
    ///
    /// The shape is checked rather than trusted because BuildTools.Capture falls back to stderr when a
    /// command writes nothing to stdout, so a half-cloned or unreadable checkout comes back as "fatal:
    /// not a git repository" instead of empty. That string is longer than a sha7 and passes any length
    /// test: `source status` printed "at fatal: " where the commit goes, and a build would have taken
    /// its first seven characters as the version it staged under.</summary>
    private static string? ReadHeadSha(string git, string checkout)
    {
        var output = BuildTools.Capture(git, ["-C", checkout, "rev-parse", "HEAD"]).Trim();
        return output.Length >= 7 && output.All(Uri.IsHexDigit) ? output : null;
    }

    private static async Task<string> FetchAsync(string git, SourceSpec spec, string checkout,
        IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);

        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            log?.Report($"cloning {spec.Repo}");
            // Blobless: the history is needed to check out a ref, the historical file contents are
            // not, and this repo's assets make a full clone many gigabytes.
            await RunAsync(git, ["clone", "--filter=blob:none", spec.Repo, checkout],
                Path.GetDirectoryName(checkout)!, log, ct, failureCode: SourceFailure.GitFailed);
        }
        else
        {
            log?.Report("fetching");
            await RunAsync(git, ["fetch", "--all", "--prune"], checkout, log, ct,
                failureCode: SourceFailure.GitFailed);
        }

        // --force also discards the nuget.config edit RepairNuGetSourcesAsync may have made last
        // build, which is what keeps that edit from accumulating or drifting.
        await RunAsync(git, ["checkout", "--force", spec.Ref], checkout, log, ct,
            failureCode: SourceFailure.GitFailed);
        // A detached tag or sha has nothing to pull, and that is not a failure.
        await RunAsync(git, ["pull", "--ff-only"], checkout, log, ct, allowFailure: true);

        var sha = ReadHeadSha(git, checkout)
                  ?? throw new SourceBuildException(SourceFailure.GitFailed,
                      $"could not read HEAD in {checkout} after checking out '{spec.Ref}'");

        log?.Report($"at {sha[..7]}");
        return sha;
    }

    /// <summary>Drop package sources that point at a directory this box does not have.
    ///
    /// The game's nuget.config adds the Godot editor's bundled nupkgs folder as a local source, which
    /// is an absolute path to one dev machine. NuGet hard-fails on a missing local source, so without
    /// this every source build fails at restore on every box that is not that machine - including all
    /// of Linux, which is where the dedicated-server preset is built. The release workflow solves it
    /// the same way, by name; this generalises to any dev-local source because the failure is the
    /// same whatever the key is called.
    ///
    /// The edit is transient: the next build's `git checkout --force` restores the file.</summary>
    private static async Task RepairNuGetSourcesAsync(string checkout, string dotnet,
        IProgress<string>? log, CancellationToken ct)
    {
        var config = GameCheckout.NuGetConfigPath(checkout);
        if (!File.Exists(config))
            return;

        XDocument document;
        try
        {
            document = XDocument.Load(config);
        }
        catch (XmlException)
        {
            return; // a malformed nuget.config is the restore's failure to report, not this method's
        }

        foreach (var source in document.Descendants("packageSources").Elements("add").ToList())
        {
            var key = source.Attribute("key")?.Value;
            var value = source.Attribute("value")?.Value;
            if (key is null || value is null || value.Contains("://", StringComparison.Ordinal))
                continue;

            var directory = Path.IsPathFullyQualified(value) ? value : Path.Combine(checkout, value);
            if (Directory.Exists(directory))
                continue;

            log?.Report($"dropping NuGet source '{key}' ({value}): not present on this box");
            await RunAsync(dotnet, ["nuget", "remove", "source", key, "--configfile", config],
                checkout, log, ct, allowFailure: true);
        }
    }

    private string Stage(string checkout, string preset, string buildId, string sha, string platformKey)
    {
        var dirName = BuildRecord.SafeDirName(buildId);
        var destination = Path.Combine(paths.VersionsDir, dirName);
        var exported = GameCheckout.DistDir(checkout, preset);

        if (!Directory.Exists(exported))
            throw new SourceBuildException(SourceFailure.PackageFailed,
                $"nothing to stage at {exported}");

        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        CopyTree(exported, Path.Combine(destination, preset));

        builds.Register(new BuildRecord
        {
            Id = buildId,
            DirName = dirName,
            Version = sha[..7],
            PlatformKey = platformKey,
            // package.sh lays the content in beside the binary, so a source build is always the
            // single-directory "complete" layout, never the shared-asset-store one.
            Layout = InstalledState.LayoutComplete,
            Root = preset,
            Provider = BuildProviders.Source,
            InstalledAt = DateTimeOffset.UtcNow,
        });

        return destination;
    }

    /// <summary>Copy a tree, preserving symlinks as symlinks.
    ///
    /// The same failure ArchiveExtractor exists for: a macOS .app's Frameworks directory is symlinks,
    /// and dereferencing them produces a bundle that looks complete and will not launch. It also keeps
    /// the copy from following a link out of the tree.</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(to, entry.Name);

            if (entry.LinkTarget is { } target)
            {
                try
                {
                    if (entry is DirectoryInfo)
                        Directory.CreateSymbolicLink(destination, target);
                    else
                        File.CreateSymbolicLink(destination, target);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Windows needs a privilege for this and Godot's Windows export has no symlinks
                    // in it, so falling through to a plain copy is right there and wrong on macOS,
                    // where the link is the point. Let the macOS case fail loudly instead.
                    if (!OperatingSystem.IsWindows())
                        throw;
                }
            }

            if (entry is DirectoryInfo directory)
                CopyTree(directory.FullName, destination);
            else
                File.Copy(entry.FullName, destination, overwrite: true);
        }
    }

    /// <summary>A game-repo script's path, checked for existence first.
    ///
    /// Without the check, a checkout too old to carry the script fails as "python: can't open file",
    /// which reads as a broken launcher. With it, the message names the file and says the ref predates
    /// the tooling, which is the actual situation.</summary>
    private static string Script(string checkout, string path, string description) =>
        Relative(checkout, GameCheckout.Require(path, description));

    /// <summary>Arguments are passed to the game's own scripts relative to the checkout, because the
    /// checkout is the working directory and a relative path keeps their messages readable.
    ///
    /// Forward slashes on every platform. Python and the Windows API both take either, but one of
    /// these arguments is a script path handed to bash, where a backslash is an escape character
    /// waiting for the wrong filename to make it matter.</summary>
    private static string Relative(string checkout, string path) =>
        Path.GetRelativePath(checkout, path).Replace('\\', '/');

    private static async Task RunAsync(string exe, IReadOnlyList<string> args, string workingDir,
        IProgress<string>? log, CancellationToken ct, bool allowFailure = false,
        string failureCode = SourceFailure.StepFailed)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new SourceBuildException(failureCode, $"could not start {exe}");
        }
        catch (Win32Exception ex)
        {
            throw new SourceBuildException(failureCode, $"could not run {exe}: {ex.Message}");
        }

        // The last few lines, so a failure message carries the reason. The whole stream reaches the
        // operator through `log`, but under --json the envelope is what a script reads, and an
        // envelope saying only "exited with 1" sends them back to scroll a build log.
        var tail = new Queue<string>();

        void Record(string? line)
        {
            if (line is null)
                return;
            log?.Report(line);
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 12)
                    tail.Dequeue();
            }
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => Record(e.Data);
            process.ErrorDataReceived += (_, e) => Record(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // A cancelled build must not leave a Godot export or a clone running. The operator
                // pressed Ctrl-C expecting the work to stop, not to detach.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                throw;
            }

            if (process.ExitCode == 0 || allowFailure)
                return;

            string[] lines;
            lock (tail)
                lines = tail.ToArray();

            throw new SourceBuildException(failureCode,
                $"{Path.GetFileName(exe)} {string.Join(' ', args)} exited with {process.ExitCode}" +
                (lines.Length == 0 ? "" : ":\n  " + string.Join("\n  ", lines)));
        }
    }
}
