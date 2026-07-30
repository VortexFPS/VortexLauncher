using System.CommandLine;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Protocol;

namespace Launcher.Cli;

/// <summary>`vortex server *`: create and operate dedicated-server instances with no web surface at
/// all. This is the whole of A2 and it is deliberately usable on its own; an operator with a headless
/// box should not have to run a control plane to run a server.</summary>
public static class ServerCommands
{
    public static void Register(RootCommand root, Option<bool> jsonOption, Option<string?> rootOption)
    {
        var server = new Command("server", "create and operate dedicated-server instances");
        server.Subcommands.Add(Create(jsonOption, rootOption));
        server.Subcommands.Add(ListInstances(jsonOption, rootOption));
        server.Subcommands.Add(Lifecycle("start", "start an instance", jsonOption, rootOption));
        server.Subcommands.Add(Lifecycle("stop", "stop an instance", jsonOption, rootOption));
        server.Subcommands.Add(Lifecycle("restart", "restart an instance", jsonOption, rootOption));
        server.Subcommands.Add(Delete(jsonOption, rootOption));
        server.Subcommands.Add(Console_(jsonOption, rootOption));
        server.Subcommands.Add(Exec(jsonOption, rootOption));
        server.Subcommands.Add(Release(jsonOption, rootOption));
        root.Subcommands.Add(server);
    }

    private static Context Open(System.CommandLine.ParseResult parse, Option<bool> jsonOption,
        Option<string?> rootOption)
    {
        var paths = new LauncherPaths(parse.GetValue(rootOption));
        RunnerIdentity.LoadOrCreate(paths);
        var store = new InstanceStore(paths);
        var supervisor = new InstanceSupervisor(store, new BuildStore(paths));
        supervisor.LoadAndAdopt();
        return new Context(new Output(parse.GetValue(jsonOption)), paths, store, supervisor);
    }

    private sealed record Context(
        Output Output, LauncherPaths Paths, InstanceStore Store, InstanceSupervisor Supervisor)
        : IDisposable
    {
        public void Dispose() => Supervisor.Dispose();
    }

    private static Command Create(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name") { Description = "instance name" };
        var map = new Option<string>("--map") { Description = "starting map", Required = true };
        var gametype = new Option<string>("--gametype")
            { Description = "gametype code", DefaultValueFactory = _ => "dm" };
        var port = new Option<int?>("--port")
            { Description = "game port; allocated from the pool when omitted" };
        var maxPlayers = new Option<int>("--max-players") { DefaultValueFactory = _ => 16 };
        var hostname = new Option<string?>("--hostname") { Description = "server browser name" };
        var buildId = new Option<string?>("--build")
            { Description = "pin a build id; the newest installed build is used when omitted" };

        var command = new Command("create", "create an instance");
        command.Arguments.Add(name);
        foreach (var option in new Option[] { map, gametype, port, maxPlayers, hostname, buildId })
            command.Options.Add(option);

        command.SetAction(parse =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instanceName = parse.GetValue(name)!;

            try
            {
                InstanceStore.ValidateName(instanceName);
            }
            catch (ArgumentException ex)
            {
                return ctx.Output.Fail("invalid_name", ex.Message, ExitCodes.Usage);
            }

            if (ctx.Store.Exists(instanceName))
                return ctx.Output.Fail("instance_exists",
                    $"instance '{instanceName}' already exists", ExitCodes.Conflict);

            var pool = new PortPool(ctx.Store);
            int chosen;
            try
            {
                chosen = parse.GetValue(port) ?? pool.Allocate();
            }
            catch (InvalidOperationException ex)
            {
                return ctx.Output.Fail("no_free_port", ex.Message, ExitCodes.Conflict);
            }

            if (parse.GetValue(port) is { } requested && pool.IsAssigned(requested))
                return ctx.Output.Fail("port_unavailable",
                    $"port {requested} is already assigned to another instance", ExitCodes.Conflict);

            var spec = new InstanceSpec
            {
                Name = instanceName,
                Map = parse.GetValue(map)!,
                Gametype = parse.GetValue(gametype)!,
                Port = chosen,
                MaxPlayers = parse.GetValue(maxPlayers),
                Hostname = parse.GetValue(hostname),
                BuildId = parse.GetValue(buildId),
            };

            ctx.Supervisor.Create(spec);
            var dir = ctx.Store.PathsFor(instanceName).Root;
            return ctx.Output.Ok(new { name = spec.Name, port = spec.Port, dir },
                $"created '{spec.Name}' on port {spec.Port} in {dir}");
        });

        return command;
    }

    private static Command ListInstances(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("list", "show instances and their state");

        command.SetAction(async parse =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var statuses = new List<InstanceStatus>();
            foreach (var instance in ctx.Supervisor.All())
            {
                if (instance.State == InstanceState.Running)
                    await instance.ProbeAsync();
                statuses.Add(instance.Status());
            }

            if (ctx.Output.IsJson)
                return ctx.Output.Ok(statuses);

            if (statuses.Count == 0)
                return ctx.Output.Ok(human: "no instances; create one with `vortex server create`");

            foreach (var s in statuses)
            {
                var players = s.Players is null ? "-" : $"{s.Players}+{s.Bots ?? 0}b/{s.MaxPlayers}";
                var mode = s.ControlMode == ControlMode.Orchestrated ? " [orchestrated]" : "";
                ctx.Output.Line($"{s.Name,-20} {s.State,-9} {s.Map ?? "-",-16} {players,-12}{mode}");
            }
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Lifecycle(string verb, string description, Option<bool> jsonOption,
        Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var command = new Command(verb, description);
        command.Arguments.Add(name);

        command.SetAction(async (parse, ct) =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instanceName = parse.GetValue(name)!;

            if (ctx.Supervisor.Find(instanceName) is null)
                return ctx.Output.Fail("instance_not_found",
                    $"no instance '{instanceName}'", ExitCodes.NotFound);

            try
            {
                switch (verb)
                {
                    case "start":
                        await ctx.Supervisor.StartAsync(instanceName, ControlOrigin.Local, ct);
                        break;
                    case "stop":
                        await ctx.Supervisor.StopAsync(
                            instanceName, ControlOrigin.Local, LocalActor(), ct: ct);
                        break;
                    default:
                        await ctx.Supervisor.RestartAsync(instanceName, ControlOrigin.Local, ct);
                        break;
                }
            }
            catch (InstanceOrchestratedException ex)
            {
                return Orchestrated(ctx.Output, ex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                return ctx.Output.Fail("operation_failed", ex.Message, ExitCodes.Error);
            }

            var status = ctx.Supervisor.Require(instanceName).Status();
            return ctx.Output.Ok(status, $"{instanceName}: {status.State}");
        });

        return command;
    }

    private static Command Delete(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var command = new Command("delete", "delete a stopped instance and its data");
        command.Arguments.Add(name);

        command.SetAction(parse =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instanceName = parse.GetValue(name)!;

            if (ctx.Supervisor.Find(instanceName) is null)
                return ctx.Output.Fail("instance_not_found",
                    $"no instance '{instanceName}'", ExitCodes.NotFound);

            try
            {
                ctx.Supervisor.Delete(instanceName, ControlOrigin.Local);
            }
            catch (InstanceOrchestratedException ex)
            {
                return Orchestrated(ctx.Output, ex);
            }
            catch (InvalidOperationException ex)
            {
                return ctx.Output.Fail("instance_running", ex.Message, ExitCodes.Conflict);
            }

            return ctx.Output.Ok(new { deleted = instanceName }, $"deleted '{instanceName}'");
        });

        return command;
    }

    private static Command Console_(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var lines = new Option<int>("--tail")
            { Description = "how many buffered lines to print first", DefaultValueFactory = _ => 100 };
        var follow = new Option<bool>("--follow") { Description = "keep streaming until interrupted" };

        var command = new Command("console", "tail an instance's output");
        command.Arguments.Add(name);
        command.Options.Add(lines);
        command.Options.Add(follow);

        command.SetAction(async (parse, ct) =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instance = ctx.Supervisor.Find(parse.GetValue(name)!);
            if (instance is null)
                return ctx.Output.Fail("instance_not_found",
                    $"no instance '{parse.GetValue(name)}'", ExitCodes.NotFound);

            var tail = instance.Tail(parse.GetValue(lines));
            if (ctx.Output.IsJson && !parse.GetValue(follow))
                return ctx.Output.Ok(tail);

            foreach (var line in tail)
                Console.WriteLine(line.Text);

            if (!parse.GetValue(follow))
                return ExitCodes.Ok;

            instance.LineWritten += line => Console.WriteLine(line.Text);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Exec(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var commandText = new Argument<string>("command") { Description = "console command to run" };

        var command = new Command("exec", "run a console command on an instance");
        command.Arguments.Add(name);
        command.Arguments.Add(commandText);

        command.SetAction(async (parse, ct) =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instanceName = parse.GetValue(name)!;
            if (ctx.Supervisor.Find(instanceName) is null)
                return ctx.Output.Fail("instance_not_found",
                    $"no instance '{instanceName}'", ExitCodes.NotFound);

            try
            {
                await ctx.Supervisor.SendAsync(instanceName, parse.GetValue(commandText)!,
                    ControlOrigin.Local, ct);
            }
            catch (InstanceOrchestratedException ex)
            {
                return Orchestrated(ctx.Output, ex);
            }
            catch (IOException ex)
            {
                return ctx.Output.Fail("no_stdin",
                    ex.Message + "; set rcon_password in server.cfg to drive an adopted instance",
                    ExitCodes.Conflict);
            }

            return ctx.Output.Ok(new { sent = parse.GetValue(commandText) }, "sent");
        });

        return command;
    }

    private static Command Release(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var name = new Argument<string>("name");
        var when = new Option<string>("--when")
        {
            Description = "end-of-match (default) or now",
            DefaultValueFactory = _ => "end-of-match",
        };
        var reason = new Option<string?>("--reason")
            { Description = "attached to the alert the orchestrator receives" };

        var command = new Command("release", "return an orchestrated instance to local control");
        command.Arguments.Add(name);
        command.Options.Add(when);
        command.Options.Add(reason);

        command.SetAction(async (parse, ct) =>
        {
            using var ctx = Open(parse, jsonOption, rootOption);
            var instance = ctx.Supervisor.Find(parse.GetValue(name)!);
            if (instance is null)
                return ctx.Output.Fail("instance_not_found",
                    $"no instance '{parse.GetValue(name)}'", ExitCodes.NotFound);

            if (instance.Spec.ControlMode == ControlMode.Local)
                return ctx.Output.Ok(new { name = instance.Name, control_mode = "local" },
                    $"'{instance.Name}' is already under local control");

            var mode = parse.GetValue(when) == "now" ? ReleaseWhen.Now : ReleaseWhen.EndOfMatch;
            await ctx.Supervisor.ReleaseAsync(instance.Name,
                new ReleaseRequest { When = mode, Reason = parse.GetValue(reason) }, LocalActor(), ct);

            var deferred = mode == ReleaseWhen.EndOfMatch && instance.MatchLive;
            return ctx.Output.Ok(
                new { name = instance.Name, when = mode, deferred },
                deferred
                    ? $"'{instance.Name}' will return to local control when the match ends"
                    : $"'{instance.Name}' is back under local control");
        });

        return command;
    }

    private static int Orchestrated(Output output, InstanceOrchestratedException ex) =>
        output.Fail(ApiErrorCodes.InstanceOrchestrated, ex.Message, ExitCodes.Conflict);

    private static string LocalActor() =>
        $"{Environment.UserName}@{Environment.MachineName} (cli)";
}
