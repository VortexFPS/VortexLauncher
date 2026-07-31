using System.CommandLine;
using Launcher.Core;
using Launcher.Core.Signing;

namespace Launcher.Cli;

/// <summary>install, update, launch: the same three things the Desktop launcher does, over the same
/// Core calls, so the CLI doubles as the integration-test surface for that path.</summary>
public static class PlayerCommands
{
    public static void Register(RootCommand root, Option<bool> jsonOption, Option<string?> rootOption)
    {
        root.Subcommands.Add(Install(jsonOption, rootOption));
        root.Subcommands.Add(Update(jsonOption, rootOption));
        root.Subcommands.Add(Launch(jsonOption, rootOption));
    }

    /// <summary>The feed call install and update share, with the one failure that must not reach
    /// System.CommandLine's default handler peeled off.
    ///
    /// A refused signature is a verdict, not a crash. Left to propagate it prints a stack trace,
    /// which under --json lands on stdout and breaks the promise that stdout is one parseable
    /// document, and which buries a message written to tell a player what happened. Both callers go
    /// through here rather than each catching it, because the one that forgets is the one that
    /// reports a tampered manifest as an unhandled exception.</summary>
    private static async Task<(ReleaseManifest? Manifest, string Detail, string? SignatureError)>
        FetchLatestAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var (manifest, detail) = await LauncherHttp.DefaultFeed(http).FetchLatestAsync(ct);
            return (manifest, detail, null);
        }
        catch (ManifestSignatureException ex)
        {
            return (null, "", ex.Message);
        }
    }

    private static Command Install(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var channel = new Option<string>("--channel")
        {
            Description = "stable or beta. beta is the only channel that sees prereleases",
            DefaultValueFactory = _ => "stable",
        };
        // --fat is the old name, kept as an alias rather than dropped: it is in whatever install
        // scripts already exist, and a flag that vanishes turns those into "unrecognised option"
        // exits. Undocumented in the description so new scripts pick up --complete.
        var complete = new Option<bool>("--complete", "--fat")
        {
            Description = "install the single-archive build instead of the split game/data payload",
        };

        var command = new Command("install", "download and install the game");
        command.Options.Add(channel);
        command.Options.Add(complete);

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            using var http = LauncherHttp.Create();
            var installs = new InstallService(paths, new DownloadService(http));

            var (manifest, detail, signatureError) = await FetchLatestAsync(http, ct);
            if (signatureError is not null)
                return output.Fail("signature_failed", signatureError, ExitCodes.VerificationFailed);
            if (manifest is null)
                return output.Fail("feed_unavailable",
                    $"no release found ({detail})", ExitCodes.Unavailable);

            if (parse.GetValue(channel) == "stable" && manifest.Prerelease)
                return output.Fail("no_stable_release",
                    $"newest release {manifest.Tag} is a prerelease; use --channel beta",
                    ExitCodes.NotFound);

            var platformKey = PlatformKey.Current;
            output.Progress($"installing {manifest.Version} ({platformKey})");

            var progress = new Progress<(string Phase, double Fraction)>(p =>
                output.Progress($"{p.Phase} {p.Fraction:P0}"));

            var state = await installs.InstallAsync(
                manifest, platformKey, preferCore: !parse.GetValue(complete), progress, ct);

            return output.Ok(
                new { version = state.Version, layout = state.Layout, dir = installs.GameDirOf(state) },
                $"installed {state.Version} ({state.Layout}) into {installs.GameDirOf(state)}");
        });

        return command;
    }

    private static Command Update(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var check = new Option<bool>("--check")
        {
            Description = "report whether an update exists and exit without installing",
        };

        var command = new Command("update", "update the installed game in place");
        command.Options.Add(check);

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            using var http = LauncherHttp.Create();
            var installs = new InstallService(paths, new DownloadService(http));
            var installed = installs.LoadCurrent();

            var (manifest, detail, signatureError) = await FetchLatestAsync(http, ct);
            if (signatureError is not null)
                return output.Fail("signature_failed", signatureError, ExitCodes.VerificationFailed);
            if (manifest is null)
                return output.Fail("feed_unavailable",
                    $"could not reach the release feed ({detail})", ExitCodes.Unavailable);

            var available = installed is null || installed.Version != manifest.Version;

            if (parse.GetValue(check))
                return output.Ok(
                    new { installed = installed?.Version, latest = manifest.Version, update_available = available },
                    available
                        ? $"update available: {installed?.Version ?? "(nothing installed)"} -> {manifest.Version}"
                        : $"up to date ({manifest.Version})");

            if (!available)
                return output.Ok(
                    new { installed = installed!.Version, update_available = false },
                    $"up to date ({installed.Version})");

            var progress = new Progress<(string Phase, double Fraction)>(p =>
                output.Progress($"{p.Phase} {p.Fraction:P0}"));

            // Stay on whatever layout is already installed. InstalledState normalizes the old "fat"
            // spelling on read, so a marker written before the rename compares correctly here.
            var state = await installs.InstallAsync(manifest, PlatformKey.Current,
                preferCore: installed?.Layout != InstalledState.LayoutComplete, progress, ct);

            return output.Ok(
                new { version = state.Version, layout = state.Layout },
                $"updated to {state.Version}");
        });

        return command;
    }

    private static Command Launch(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var connect = new Option<string?>("--connect")
        {
            Description = "join a server on start, host[:port]",
        };
        var gameArgs = new Argument<string[]>("game-args")
        {
            Description = "arguments passed through to the game, after --",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("launch", "run the installed game");
        command.Options.Add(connect);
        command.Arguments.Add(gameArgs);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            using var http = LauncherHttp.Create();
            var installs = new InstallService(paths, new DownloadService(http));

            var installed = installs.LoadCurrent();
            if (installed is null)
                return output.Fail("not_installed",
                    "nothing installed; run `vortex install` first", ExitCodes.NotInstalled);

            var extra = new List<string>();
            if (parse.GetValue(connect) is { Length: > 0 } target)
            {
                extra.Add("+connect");
                extra.Add(target);
            }
            extra.AddRange(parse.GetValue(gameArgs) ?? []);

            try
            {
                var process = new GameLauncher(installs).Launch(installed, extra);
                return output.Ok(
                    new { version = installed.Version, pid = process.Id },
                    $"launched {installed.Version} (pid {process.Id})");
            }
            catch (FileNotFoundException ex)
            {
                return output.Fail("binary_missing", ex.Message, ExitCodes.Error);
            }
        });

        return command;
    }
}
