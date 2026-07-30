using System.CommandLine;
using Launcher.Core;

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

    private static Command Install(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var channel = new Option<string>("--channel")
        {
            Description = "stable or beta. beta is the only channel that sees prereleases",
            DefaultValueFactory = _ => "stable",
        };
        var fat = new Option<bool>("--fat")
        {
            Description = "install the single-archive build instead of the split game/data payload",
        };

        var command = new Command("install", "download and install the game");
        command.Options.Add(channel);
        command.Options.Add(fat);

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            using var http = LauncherHttp.Create();
            var installs = new InstallService(paths, new DownloadService(http));

            var (manifest, detail) = await LauncherHttp.DefaultFeed(http).FetchLatestAsync(ct);
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
                manifest, platformKey, preferCore: !parse.GetValue(fat), progress, ct);

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

            var (manifest, detail) = await LauncherHttp.DefaultFeed(http).FetchLatestAsync(ct);
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

            var state = await installs.InstallAsync(manifest, PlatformKey.Current,
                preferCore: installed?.Layout != InstalledState.LayoutFat, progress, ct);

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
