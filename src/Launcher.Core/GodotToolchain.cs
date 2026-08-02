using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>Finding the external programs a source build shells out to.
///
/// Every lookup here fails loudly and names what to install. None of them falls back to "try anyway":
/// a build that proceeds without the thing it needed either dies later with a worse message or, in the
/// engine's case, succeeds and ships the wrong binary.</summary>
public static partial class BuildTools
{
    /// <summary>First match on PATH, as an absolute path. Resolved here rather than left to
    /// Process.Start so the CLI can report which binary it is about to run, and so "not installed"
    /// becomes a message instead of a Win32Exception.</summary>
    public static string? FindOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + extension);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { } // a malformed PATH entry is not this method's problem
            }
        }

        return null;
    }

    public static string RequireOnPath(string name, string code, string install) =>
        FindOnPath(name) ?? throw new SourceBuildException(code,
            $"{name} was not found on PATH, and a source build needs it. {install}");

    /// <summary>Python 3, which is what the game repo's build tooling is written in.
    ///
    /// The launcher calls those scripts instead of porting them, so this is a hard requirement rather
    /// than a nicety. Porting fetch-engine-template.py into C# would give the project two downloaders
    /// with one lockfile between them, and the day they disagree is the day a local build ships a
    /// stock engine while CI ships a patched one.</summary>
    public static string ResolvePython()
    {
        if (Environment.GetEnvironmentVariable("VORTEX_PYTHON") is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : throw new SourceBuildException(
                SourceFailure.PythonMissing,
                $"VORTEX_PYTHON is set to '{configured}', which does not exist");

        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "python3", "python", "py" }
                     : new[] { "python3", "python" })
        {
            if (FindOnPath(name) is not { } path)
                continue;
            // "python" is Python 2 on some boxes and a Windows Store stub on others, and both would
            // fail deep inside a script with an unhelpful message.
            if (Capture(path, ["--version"]).Contains("Python 3", StringComparison.Ordinal))
                return path;
        }

        throw new SourceBuildException(SourceFailure.PythonMissing,
            "no Python 3 on PATH. A source build runs the game repo's own tooling " +
            "(tools/data/fetch-engine-template.py, tools/verify-engine-template.py) rather than a " +
            "second copy of it inside the launcher, so Python 3 is required. Install it from " +
            "https://www.python.org/downloads/ or your package manager, or set VORTEX_PYTHON.");
    }

    /// <summary>bash, for tools/package.sh.
    ///
    /// Git Bash is preferred on Windows and the first PATH hit is deliberately NOT trusted there: on a
    /// default Windows install that hit is C:\Windows\System32\bash.exe, which is the WSL launcher
    /// rather than a shell on this machine. It would run package.sh inside a Linux distro, where the
    /// checkout is /mnt/c/..., the tools the script probes for are a different set, and the layout it
    /// writes lands with Linux permissions. Measured on this box: that bash reports
    /// x86_64-pc-linux-gnu. Git for Windows ships a real one and git is already required, so this is
    /// nearly always satisfiable; it is looked up beside git.exe because the installer's default PATH
    /// entry covers cmd/ and not bin/.</summary>
    public static string ResolveBash()
    {
        foreach (var candidate in GitBashCandidates())
            if (File.Exists(candidate))
                return candidate;

        if (FindOnPath("bash") is { } onPath && !IsWslLauncher(onPath))
            return onPath;

        throw new SourceBuildException(SourceFailure.BashMissing,
            "no usable bash found. The build lays content out with the checkout's own " +
            "tools/package.sh, so that a source build and a release build produce the same directory. " +
            "On Windows install Git for Windows, which ships one; the bash in System32 is the WSL " +
            "launcher and is deliberately not used, because it runs the script in a different " +
            "filesystem. On Linux and macOS install bash.");
    }

    private static IEnumerable<string> GitBashCandidates()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        if (FindOnPath("git") is { } git && Path.GetDirectoryName(git) is { } gitDir &&
            Directory.GetParent(gitDir)?.FullName is { } gitRoot)
        {
            yield return Path.Combine(gitRoot, "bin", "bash.exe");
            yield return Path.Combine(gitRoot, "usr", "bin", "bash.exe");
        }

        yield return @"C:\Program Files\Git\bin\bash.exe";
        yield return @"C:\Program Files (x86)\Git\bin\bash.exe";
    }

    private static bool IsWslLauncher(string path) =>
        OperatingSystem.IsWindows() &&
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.System),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Run something and return its stdout, swallowing every failure.
    ///
    /// Only for probes, where "it did not run" and "it printed nothing useful" lead to the same
    /// place: the caller rejects the candidate and says so.</summary>
    internal static string Capture(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return "";

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000))
                return "";
            // Godot writes its version banner to stdout, python -V to stdout on 3.4+ and stderr
            // before it; take whichever came back.
            return stdout.Length > 0 ? stdout : stderr;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }
}

/// <summary>A Godot editor binary on this box, and whether it is the one the checkout pins.
///
/// The editor and the export template are different things and the distinction is the whole point of
/// this type. The TEMPLATE is what gets embedded in the shipped game, so it must be the patched one
/// the checkout pins and it is fetched from the checkout's own lockfile. The EDITOR only drives the
/// export, so it can be a stock download; but it must still be the same VERSION, because Godot writes
/// the project with the editor's own resource formats and hands the result to the template.
///
/// The launcher does not download an editor. The game's release only publishes templates, there is no
/// pinned editor artifact to fetch, and inventing one - a stock godotengine.org download resolved by
/// guessing a mirror URL and an archive layout - would be a second unpinned acquisition path for the
/// one input this whole mechanism exists to control. So a missing editor is a refusal that names what
/// to install, never a silent fallback.</summary>
public sealed partial record GodotEditor
{
    public required string Path { get; init; }
    public required string RawVersion { get; init; }

    /// <summary>Numeric part only: "4.6.3".</summary>
    public required string Version { get; init; }

    /// <summary>Godot's status field: stable, beta3, rc1.</summary>
    public string? Channel { get; init; }

    /// <summary>Whether this is the mono/.NET build. The plain build cannot run or export C# at
    /// all.</summary>
    public bool Mono { get; init; }

    /// <summary>Resolve an editor: explicit path, then the environment, then the checkout's own
    /// .godot-bin/, then PATH.
    ///
    /// Discovery is last and never silent - whatever it lands on gets its version checked against the
    /// checkout's pin before anything is built with it.</summary>
    /// <param name="checkout">The checkout being built, so its repo-local engine can be found. Optional
    /// because the resolution order above is still meaningful without one.</param>
    public static GodotEditor Resolve(string? explicitPath, string? checkout = null)
    {
        var (path, origin) = Locate(explicitPath, checkout);
        var raw = FirstVersionLine(BuildTools.Capture(path, ["--version"]));

        if (raw is null)
            throw new SourceBuildException(SourceFailure.EditorUnusable,
                $"'{path}' ({origin}) did not answer --version with a Godot version string. On Windows " +
                "the plain editor detaches from the terminal and prints nothing a caller can read; use " +
                "the _console build (docs/RUNNING.md names it). Otherwise this is not a Godot editor.");

        var numeric = string.Join('.', raw.Split('.').TakeWhile(p => int.TryParse(p, out _)));
        var rest = raw.Split('.').SkipWhile(p => int.TryParse(p, out _)).ToArray();

        return new GodotEditor
        {
            Path = path,
            RawVersion = raw,
            Version = numeric,
            Channel = rest.FirstOrDefault(),
            Mono = rest.Contains("mono", StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Refuse unless this editor is the engine the checkout pins.
    ///
    /// Both versions are always named. Skew here is not a near miss to warn about and carry on from:
    /// the project loads, the C# compiles, the export succeeds, and the mismatch surfaces as resource
    /// format or ABI misbehaviour at runtime on a player's machine. Refusing costs one message;
    /// proceeding costs an afternoon of debugging the wrong layer.</summary>
    public void RequireMatches(EnginePin pin, string lockfilePath)
    {
        if (!VersionsAgree(pin.Version, Version))
            throw new SourceBuildException(SourceFailure.EngineSkew,
                $"engine version skew: this checkout pins Godot {pin.Version}" +
                $"{(pin.Channel is null ? "" : $" ({pin.Channel})")} in {lockfilePath}, and the editor " +
                $"at {Path} is {RawVersion}. Refusing to build. Install Godot {pin.Version} " +
                (pin.RequiresDotnet ? "(mono/.NET build) " : "") +
                "and point --godot at it, or check out a ref whose lockfile pins " +
                $"{Version}. There is no 'try anyway': a build against a mismatched engine compiles " +
                "and then misbehaves at runtime, which is far more expensive than this refusal.");

        if (pin.Channel is { Length: > 0 } channel && Channel is { Length: > 0 } actual &&
            !string.Equals(channel, actual, StringComparison.OrdinalIgnoreCase))
            throw new SourceBuildException(SourceFailure.EngineSkew,
                $"engine channel skew: this checkout pins Godot {pin.Version}.{channel} in " +
                $"{lockfilePath}, and the editor at {Path} is {RawVersion}. A prerelease editor and a " +
                "stable pin are different engines with the same version number. Refusing to build.");

        if (pin.RequiresDotnet && !Mono)
            throw new SourceBuildException(SourceFailure.EngineSkew,
                $"the editor at {Path} is {RawVersion}, which is not the mono/.NET build, and " +
                $"{lockfilePath} pins engine.dotnet. The plain editor cannot build or export a C# " +
                $"project. Install Godot {pin.Version} mono from https://godotengine.org/download.");
    }

    /// <summary>Compare on the numeric parts, padded, so 4.6 and 4.6.0 are the same engine. Godot
    /// omits a zero patch from its own version string.</summary>
    internal static bool VersionsAgree(string pinned, string actual)
    {
        static int[] Parts(string v) =>
            v.Split('.').TakeWhile(p => int.TryParse(p, out _)).Select(int.Parse)
                .Concat([0, 0, 0]).Take(3).ToArray();

        return Parts(pinned).SequenceEqual(Parts(actual));
    }

    private static (string Path, string Origin) Locate(string? explicitPath, string? checkout)
    {
        if (explicitPath is { Length: > 0 })
            return File.Exists(explicitPath)
                ? (PreferConsoleBuild(System.IO.Path.GetFullPath(explicitPath)), "--godot")
                : throw new SourceBuildException(SourceFailure.EditorMissing,
                    $"no Godot editor at '{explicitPath}'. That path came from --godot or from " +
                    "`vortex source set --godot`; fix it or clear it to fall back to PATH.");

        foreach (var variable in new[] { "VORTEX_GODOT", "GODOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv)
                return File.Exists(fromEnv)
                    ? (PreferConsoleBuild(System.IO.Path.GetFullPath(fromEnv)), $"${variable}")
                    : throw new SourceBuildException(SourceFailure.EditorMissing,
                        $"${variable} is set to '{fromEnv}', which does not exist");

        // The checkout's own engine, where `vx setup` installs it and where the game repo's
        // find-godot.sh and vx's Env.FindGodot both probe before PATH. Ahead of PATH here for the
        // reason that directory exists at all: two checkouts at two refs can pin two engine versions,
        // and only a per-checkout install can express that. A PATH hit is one engine shared by every
        // source an operator has registered, which is the assumption .godot-bin/ was added to break.
        // The candidate names are vx's list verbatim, because a third resolver that probes a fourth
        // set of paths is how "which Godot did it actually use" stops having one answer.
        if (checkout is { Length: > 0 })
        {
            var bin = System.IO.Path.Combine(checkout, ".godot-bin");
            foreach (var candidate in new[]
                     {
                         System.IO.Path.Combine(bin, "godot_console.exe"),
                         System.IO.Path.Combine(bin, "godot.exe"),
                         System.IO.Path.Combine(bin, "Godot.app", "Contents", "MacOS", "Godot"),
                         System.IO.Path.Combine(bin, "godot"),
                     })
                if (File.Exists(candidate))
                    return (candidate, ".godot-bin/ in the checkout");
        }

        foreach (var name in new[] { "godot4", "godot", "Godot" })
            if (BuildTools.FindOnPath(name) is { } found)
                return (PreferConsoleBuild(found), $"PATH ({name})");

        foreach (var candidate in PlatformInstallCandidates())
            if (File.Exists(candidate))
                return (PreferConsoleBuild(candidate), "the platform install location");

        throw new SourceBuildException(SourceFailure.EditorMissing,
            "no Godot editor found, and a source build needs one to drive the export.\n" +
            "  The export TEMPLATE that ships inside the game is fetched from the checkout's own pin " +
            "and verified against it. The EDITOR is a separate program that runs the export, it is not " +
            "published by this project, and it has to come from this box.\n" +
            "  Install the mono/.NET build of the Godot version the checkout pins from " +
            "https://godotengine.org/download (`vortex source status <name>` prints which version), " +
            "then either put it on PATH as `godot`, set $VORTEX_GODOT, or record it with " +
            "`vortex source set <name> --godot <path>`.\n" +
            "  A checkout carrying ./vx can also install its own pinned editor into .godot-bin/ with " +
            "`./vx setup --profile dev`, which this resolver looks in ahead of PATH — that is the one " +
            "option which gets the version right by construction rather than by the check below.\n" +
            "  On Windows use the _console build: the plain one detaches from the terminal and the " +
            "build output goes nowhere.");
    }

    /// <summary>Where an editor is if it was installed normally, probed after PATH.
    ///
    /// vx's Env.FindGodot and the game repo's find-godot.sh both look here and this resolver did not.
    /// On the dev box that meant vx reported a usable 4.6.3 mono editor at C:\Program Files\Godot while
    /// a source build on the same machine refused for having no editor at all — two answers about one
    /// box, and the launcher's was the wrong one. An installer that does not touch PATH is the normal
    /// case on Windows and macOS, so this is most machines rather than an edge.
    ///
    /// Windows is enumerated rather than spelled out, which is the one deliberate difference from vx.
    /// vx names the pinned filename literally, and that is right for a tool that lives in the repo
    /// doing the pinning; the launcher builds arbitrary refs pinning arbitrary versions, so it takes
    /// what is installed and lets <see cref="RequireMatches"/> be the thing with an opinion about the
    /// version. Newest first, so a box with two installs starts from the likelier one and gets a skew
    /// message naming a real alternative rather than the oldest thing on disk.</summary>
    private static IEnumerable<string> PlatformInstallCandidates()
    {
        if (OperatingSystem.IsMacOS())
            return ["/Applications/Godot_mono.app/Contents/MacOS/Godot",
                "/Applications/Godot.app/Contents/MacOS/Godot"];

        if (!OperatingSystem.IsWindows())
            return ["/usr/local/bin/godot", "/usr/bin/godot"];

        var found = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (root.Length == 0)
                continue;

            var dir = System.IO.Path.Combine(root, "Godot");
            try
            {
                if (!Directory.Exists(dir))
                    continue;

                // Console builds first: the plain one detaches from the terminal, so --version comes
                // back empty and the export log goes nowhere. PreferConsoleBuild would swap to the twin
                // anyway; ordering here means the twin is what gets probed rather than what gets fixed.
                found.AddRange(Directory.EnumerateFiles(dir, "Godot_v*.exe")
                    .OrderByDescending(f =>
                        f.Contains("_console", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A Program Files this account cannot read is not this method's problem to report; the
                // refusal below already says no editor was found and names every way to supply one.
            }
        }

        return found;
    }

    /// <summary>On Windows, prefer Godot_..._console.exe beside the plain binary.
    ///
    /// The plain build detaches from the console, so a redirected stdout comes back empty: --version
    /// returns nothing and the export log is lost. docs/RUNNING.md makes the same choice for the same
    /// reason. Applied to an operator-supplied path too, not just to a PATH hit: the GUI binary is the
    /// one they have a shortcut to, and silently using its console twin is better than refusing a
    /// perfectly good install over a filename.</summary>
    private static string PreferConsoleBuild(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return path;

        foreach (var suffix in new[] { "_console.exe", ".console.exe" })
        {
            var console = path[..^4] + suffix;
            if (File.Exists(console))
                return console;
        }

        return path;
    }

    /// <summary>Godot prints "4.6.3.stable.mono.official.&lt;hash&gt;". Some builds print a line or two
    /// before it, so take the first line that starts with a version rather than the first line.</summary>
    private static string? FirstVersionLine(string output) =>
        output.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => VersionLinePattern().IsMatch(line));

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?\.")]
    private static partial Regex VersionLinePattern();
}
