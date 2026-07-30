using System.CommandLine;
using Launcher.Core;

namespace Launcher.Cli;

/// <summary>The build store: what is on disk, which one is live, and reclaiming the rest. Rollback is
/// `builds pin &lt;previous&gt;`, which is a file write rather than a reinstall because the previous
/// build is deliberately still there.</summary>
public static class BuildCommands
{
    public static void Register(RootCommand root, Option<bool> jsonOption, Option<string?> rootOption)
    {
        var builds = new Command("builds", "installed builds: list, pin, reclaim");
        builds.Subcommands.Add(List(jsonOption, rootOption));
        builds.Subcommands.Add(Pin(jsonOption, rootOption));
        builds.Subcommands.Add(Gc(jsonOption, rootOption));
        root.Subcommands.Add(builds);
    }

    private static Command List(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("list", "show installed builds");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new BuildStore(paths);
            var current = new InstallService(paths, new NullDownloader(), store).LoadCurrent();

            var rows = store.List().Select(b => new
            {
                id = b.Id,
                version = b.Version,
                provider = b.Provider,
                platform = b.PlatformKey,
                layout = b.Layout,
                size_bytes = store.SizeOf(b),
                installed_at = b.InstalledAt,
                current = current is not null && current.Version == b.Version,
            }).ToList();

            if (output.IsJson)
                return output.Ok(rows);

            if (rows.Count == 0)
                return output.Ok(human: "no builds installed");

            foreach (var r in rows)
                output.Line($"{(r.current ? "*" : " ")} {r.id,-24} {r.provider,-8} " +
                            $"{r.layout,-5} {r.size_bytes / (1 << 20),6} MB  {r.installed_at:yyyy-MM-dd}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Pin(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var id = new Argument<string>("id") { Description = "build id, as shown by `vortex builds list`" };

        var command = new Command("pin", "make an installed build the one that launches");
        command.Arguments.Add(id);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new BuildStore(paths);
            var installs = new InstallService(paths, new NullDownloader(), store);

            var wanted = parse.GetValue(id)!;
            var build = store.Get(wanted);
            if (build is null)
                return output.Fail("build_not_found",
                    $"no build '{wanted}'; `vortex builds list` shows what is installed",
                    ExitCodes.NotFound);

            try
            {
                var state = installs.Pin(build);
                return output.Ok(
                    new { id = build.Id, version = state.Version, layout = state.Layout },
                    $"pinned {build.Id}");
            }
            catch (InvalidOperationException ex)
            {
                return output.Fail("build_incomplete", ex.Message, ExitCodes.Error);
            }
        });

        return command;
    }

    private static Command Gc(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var keep = new Option<int>("--keep")
        {
            Description = "how many builds to keep, newest first. The pinned build is always kept",
            DefaultValueFactory = _ => BuildStore.DefaultKeep,
        };
        var dryRun = new Option<bool>("--dry-run") { Description = "report what would be removed" };

        var command = new Command("gc", "delete builds beyond the keep count");
        command.Options.Add(keep);
        command.Options.Add(dryRun);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new BuildStore(paths);
            var current = new InstallService(paths, new NullDownloader(), store).LoadCurrent();
            var keepCount = Math.Max(1, parse.GetValue(keep));

            if (parse.GetValue(dryRun))
            {
                var all = store.List();
                var survivors = new HashSet<string>(StringComparer.Ordinal);
                if (current is not null)
                    survivors.Add(current.Version);
                foreach (var b in all.Where(b => !survivors.Contains(b.Id))
                             .Take(Math.Max(0, keepCount - survivors.Count)))
                    survivors.Add(b.Id);

                var doomed = all.Where(b => !survivors.Contains(b.Id)).Select(b => b.Id).ToList();
                return output.Ok(new { would_remove = doomed },
                    doomed.Count == 0 ? "nothing to remove" : "would remove " + string.Join(", ", doomed));
            }

            var removed = store.Gc(keepCount, current?.Version);
            return output.Ok(new { removed },
                removed.Count == 0 ? "nothing to remove" : "removed " + string.Join(", ", removed));
        });

        return command;
    }

    /// <summary>Build-store verbs never download. Handing them a downloader that throws is more honest
    /// than one that quietly works, because an install path reached from `builds gc` would be a bug.</summary>
    private sealed class NullDownloader : IDownloader
    {
        public Task DownloadAsync(string url, string destPath, long expectedSize,
            string? expectedSha256, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("build-store commands do not download");
    }
}
