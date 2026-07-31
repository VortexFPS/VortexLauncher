using System.CommandLine;
using Launcher.Core;

namespace Launcher.Cli;

/// <summary>`vortex source *`: build the game from a git checkout instead of downloading a release.
///
/// The result is a normal entry in the build store, so `builds list`, `builds pin`, `builds gc` and
/// `server create --build` treat a compiled build exactly like a downloaded one. That is the whole
/// point of routing it through the store rather than giving source builds their own lifecycle.
///
/// `build` is the long verb and it streams: every line the toolchain prints goes to stderr, which
/// keeps stdout a single JSON document under --json and keeps the operator informed of a job that
/// takes tens of minutes.</summary>
public static class SourceCommands
{
    public static void Register(RootCommand root, Option<bool> jsonOption, Option<string?> rootOption)
    {
        var source = new Command("source", "build the game from a git checkout");
        source.Subcommands.Add(Set(jsonOption, rootOption));
        source.Subcommands.Add(ListSources(jsonOption, rootOption));
        source.Subcommands.Add(Status(jsonOption, rootOption));
        source.Subcommands.Add(Build(jsonOption, rootOption));
        source.Subcommands.Add(Remove(jsonOption, rootOption));
        root.Subcommands.Add(source);
    }

    /// <summary>Map a build failure to an exit code, so a script can tell the three cases apart
    /// without reading prose: fix this box (4), fix the command (2), the build itself is bad (1), and
    /// the engine is not what it claims (7).</summary>
    private static int ExitCodeFor(string? code) => code switch
    {
        SourceFailure.GitMissing or SourceFailure.DotnetMissing or SourceFailure.PythonMissing
            or SourceFailure.BashMissing or SourceFailure.EditorMissing
            or SourceFailure.EditorUnusable => ExitCodes.NotInstalled,

        SourceFailure.PresetUnknown or SourceFailure.CrossPlatform => ExitCodes.Usage,

        // Engine skew is a verification failure, not a build failure: the compiler was happy and the
        // thing that is wrong is which engine went in.
        SourceFailure.EngineSkew or SourceFailure.VerificationFailed => ExitCodes.VerificationFailed,

        _ => ExitCodes.Error,
    };

    private static Command Set(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name") { Description = "short name for this repo and ref" };
        var repo = new Option<string?>("--repo")
        {
            Description = "git URL; defaults to the game repo, forks are the point of the flag",
        };
        var reference = new Option<string?>("--ref") { Description = "branch, tag or sha" };
        var target = new Option<string?>("--target")
        {
            Description = "export preset to build; defaults to this platform's " +
                          $"({SourceProvider.DefaultPreset()})",
        };
        var godot = new Option<string?>("--godot")
        {
            Description = "path to the Godot editor that drives the export, when it is not on PATH",
        };

        var command = new Command("set", "create or update a named source");
        command.Arguments.Add(name);
        foreach (var option in new Option[] { repo, reference, target, godot })
            command.Options.Add(option);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new SourceStore(paths);
            var sourceName = parse.GetValue(name)!;

            try
            {
                SourceStore.ValidateName(sourceName);
            }
            catch (ArgumentException ex)
            {
                return output.Fail("invalid_name", ex.Message, ExitCodes.Usage);
            }

            // Update in place: `source set web --ref v1.2` keeps the repo it was pointed at. A verb
            // called "set" that silently reset the fields it was not given would make changing a ref
            // a two-flag operation nobody would remember to complete.
            var existing = store.Get(sourceName);
            var spec = new SourceSpec
            {
                Name = sourceName,
                Repo = parse.GetValue(repo) ?? existing?.Repo ?? $"{LauncherConfig.RepoUrl}.git",
                Ref = parse.GetValue(reference) ?? existing?.Ref ?? "main",
                Target = parse.GetValue(target) ?? existing?.Target,
                GodotPath = parse.GetValue(godot) ?? existing?.GodotPath,
                LastBuildId = existing?.LastBuildId,
                LastBuiltSha = existing?.LastBuiltSha,
                LastBuiltAt = existing?.LastBuiltAt,
            };

            store.Save(spec);

            return output.Ok(
                new
                {
                    name = spec.Name,
                    repo = spec.Repo,
                    @ref = spec.Ref,
                    target = spec.Target ?? SourceProvider.DefaultPreset(),
                    godot = spec.GodotPath,
                    created = existing is null,
                },
                $"{(existing is null ? "created" : "updated")} source '{spec.Name}': " +
                $"{spec.Repo} @ {spec.Ref} -> {spec.Target ?? SourceProvider.DefaultPreset()}");
        });

        return command;
    }

    private static Command ListSources(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("list", "show configured sources");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var specs = new SourceStore(paths).List();

            if (output.IsJson)
                return output.Ok(specs.Select(s => new
                {
                    name = s.Name,
                    repo = s.Repo,
                    @ref = s.Ref,
                    target = s.Target ?? SourceProvider.DefaultPreset(),
                    godot = s.GodotPath,
                    last_build_id = s.LastBuildId,
                    last_built_at = s.LastBuiltAt,
                }).ToList());

            if (specs.Count == 0)
                return output.Ok(human:
                    "no sources; add one with `vortex source set <name> --repo <url> --ref <ref>`");

            foreach (var s in specs)
                output.Line($"{s.Name,-16} {s.Ref,-20} {s.Target ?? SourceProvider.DefaultPreset(),-16} " +
                            $"{s.LastBuildId ?? "(never built)"}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Status(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");

        var command = new Command("status",
            "report whether this box can build a source, and against which engine");
        command.Arguments.Add(name);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new SourceStore(paths);

            var spec = store.Get(parse.GetValue(name)!);
            if (spec is null)
                return output.Fail("source_not_found",
                    $"no source '{parse.GetValue(name)}'; `vortex source list` shows what is configured",
                    ExitCodes.NotFound);

            var report = new SourceProvider(paths, new BuildStore(paths)).Inspect(spec);

            if (output.IsJson)
            {
                output.Ok(new
                {
                    name = report.Name,
                    repo = report.Repo,
                    @ref = report.Ref,
                    checkout = report.Checkout,
                    checked_out = report.CheckedOut,
                    sha = report.Sha,
                    preset = report.Preset,
                    platform_key = report.PlatformKey,
                    engine_version = report.EngineVersion,
                    engine_tag = report.EngineTag,
                    template_file = report.TemplateFile,
                    template_present = report.TemplatePresent,
                    tools = report.Tools.Select(t => new { t.Name, ok = t.Ok, path = t.Path, t.Problem }),
                    ready = report.Ready,
                    problems = report.Problems,
                    last_build_id = report.LastBuildId,
                    last_built_at = report.LastBuiltAt,
                });
                // The envelope already says ok:true - the command ran. The exit code is the answer to
                // the different question the verb is for: can this box build it right now.
                return report.Ready ? ExitCodes.Ok : ExitCodes.NotInstalled;
            }

            output.Line($"{report.Name}: {report.Repo} @ {report.Ref}");
            output.Line($"  checkout   {report.Checkout}" +
                        (report.Sha is null ? " (not cloned yet)" : $" at {report.Sha[..7]}"));
            output.Line($"  target     {report.Preset} ({report.PlatformKey ?? "unmapped"})");
            output.Line($"  engine     {report.EngineVersion ?? "?"} " +
                        $"pinned by {report.EngineTag ?? "(no template tag)"}");
            output.Line($"  template   {report.TemplateFile ?? "?"} " +
                        (report.TemplatePresent ? "cached" : "not fetched yet (the build fetches it)"));

            foreach (var tool in report.Tools)
                // Not "MISSING": the editor can be present and still refused for version skew, and a
                // line that says missing sends the operator to install a second copy of what they
                // already have.
                output.Line($"  {tool.Name,-10} {(tool.Ok ? tool.Path : "unusable (see below)")}");

            if (report.LastBuildId is not null)
                output.Line($"  last build {report.LastBuildId} ({report.LastBuiltAt:yyyy-MM-dd HH:mm})");

            if (report.Ready)
            {
                output.Line($"ready: `vortex source build {report.Name}`");
                return ExitCodes.Ok;
            }

            output.Line("");
            output.Line("not ready:");
            foreach (var problem in report.Problems)
                // These messages are several lines long on purpose - they say what to install and
                // why - so the continuation is indented under the bullet rather than left to run
                // back to column zero and read as a new item.
                output.Line("  - " + problem.Replace("\n", "\n    "));
            return ExitCodes.NotInstalled;
        });

        return command;
    }

    private static Command Build(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var target = new Option<string?>("--target")
        {
            Description = "export preset to build, overriding the one recorded by `source set`",
        };
        var godot = new Option<string?>("--godot")
        {
            Description = "path to the Godot editor, overriding the one recorded by `source set`",
        };
        var skipMaps = new Option<bool>("--skip-maps")
        {
            Description = "do not run tools/data/fetch-maps.py; the build ships without playable maps " +
                          "unless the checkout already has them",
        };

        var command = new Command("build", "compile a source and stage it in the build store");
        command.Arguments.Add(name);
        foreach (var option in new Option[] { target, godot, skipMaps })
            command.Options.Add(option);

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new SourceStore(paths);

            var spec = store.Get(parse.GetValue(name)!);
            if (spec is null)
                return output.Fail("source_not_found",
                    $"no source '{parse.GetValue(name)}'; add one with " +
                    "`vortex source set <name> --repo <url> --ref <ref>`",
                    ExitCodes.NotFound);

            // Flags override the stored spec for this run without rewriting it. Building one branch
            // for the client preset once should not silently repoint the source.
            var effective = spec with
            {
                Target = parse.GetValue(target) ?? spec.Target,
                GodotPath = parse.GetValue(godot) ?? spec.GodotPath,
            };

            var provider = new SourceProvider(paths, new BuildStore(paths));
            var log = new Progress<string>(output.Progress);
            var started = DateTimeOffset.UtcNow;

            var result = await provider.BuildAsync(effective, !parse.GetValue(skipMaps), log, ct);

            if (!result.Ok)
                return output.Fail(result.Code ?? SourceFailure.StepFailed,
                    result.Error ?? "the build failed", ExitCodeFor(result.Code));

            store.Save(spec with
            {
                LastBuildId = result.BuildId,
                LastBuiltSha = result.Sha,
                LastBuiltAt = DateTimeOffset.UtcNow,
            });

            var elapsed = DateTimeOffset.UtcNow - started;
            return output.Ok(
                new
                {
                    build_id = result.BuildId,
                    sha = result.Sha,
                    preset = result.Preset,
                    platform_key = result.PlatformKey,
                    engine_version = result.EngineVersion,
                    engine_tag = result.EngineTag,
                    editor = new { path = result.EditorPath, version = result.EditorVersion },
                    dir = result.Dir,
                    seconds = (int)elapsed.TotalSeconds,
                },
                $"built {result.BuildId} in {(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s; " +
                $"`vortex builds pin {result.BuildId}` to run it");
        });

        return command;
    }

    private static Command Remove(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var purge = new Option<bool>("--purge")
        {
            Description = "also delete the checkout, which is gigabytes and is otherwise kept",
        };

        var command = new Command("remove", "forget a source");
        command.Arguments.Add(name);
        command.Options.Add(purge);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var store = new SourceStore(paths);
            var sourceName = parse.GetValue(name)!;

            if (!store.Exists(sourceName))
                return output.Fail("source_not_found", $"no source '{sourceName}'", ExitCodes.NotFound);

            // Builds already staged are left alone: they are in the build store like any other build,
            // an instance may be pinned to one, and `builds gc` is the verb that reclaims them.
            var provider = new SourceProvider(paths, new BuildStore(paths));
            var checkout = provider.CheckoutFor(sourceName);
            var purged = false;

            if (parse.GetValue(purge) && Directory.Exists(checkout))
            {
                try
                {
                    provider.DeleteCheckout(sourceName);
                    purged = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return output.Fail("checkout_busy",
                        $"removed nothing: {checkout} could not be deleted ({ex.Message})",
                        ExitCodes.Conflict);
                }
            }

            store.Delete(sourceName);
            return output.Ok(new { removed = sourceName, purged, checkout },
                purged
                    ? $"removed '{sourceName}' and deleted {checkout}"
                    : $"removed '{sourceName}'; the checkout is still at {checkout} (--purge deletes it)");
        });

        return command;
    }
}
