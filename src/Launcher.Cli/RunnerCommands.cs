using System.CommandLine;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Protocol;

namespace Launcher.Cli;

/// <summary>`vortex runner *`: the daemon half of this binary.
///
/// `vortex runner run` is what the systemd unit or Windows service executes. It is the same
/// executable an operator types verbs into, which means one thing to install, version and package on
/// a host box.</summary>
public static class RunnerCommands
{
    public static void Register(RootCommand root, Option<bool> jsonOption, Option<string?> rootOption)
    {
        var runner = new Command("runner", "the supervisor daemon and its link to a control plane");
        runner.Subcommands.Add(Run(jsonOption, rootOption));
        runner.Subcommands.Add(Status(jsonOption, rootOption));
        runner.Subcommands.Add(Link(jsonOption, rootOption));
        runner.Subcommands.Add(Unlink(jsonOption, rootOption));
        runner.Subcommands.Add(RotateKey(jsonOption, rootOption));
        runner.Subcommands.Add(InstallService(jsonOption, rootOption));
        root.Subcommands.Add(runner);
    }

    private static Command Run(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("run", "run the supervisor in the foreground");

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var runnerId = RunnerIdentity.LoadOrCreate(paths);
            var config = new RunnerConfigStore(paths).Load();

            var store = new InstanceStore(paths);
            using var supervisor = new InstanceSupervisor(store, new BuildStore(paths));

            // Adopt before anything else. A runner restart to pick up a new version must not take a
            // server full of players down with it, so re-attaching to live children is the normal
            // startup path rather than a recovery one.
            supervisor.LoadAndAdopt();
            supervisor.StartHealthLoop(TimeSpan.FromSeconds(15), ct);

            var adopted = supervisor.All().Count(i => i.State == InstanceState.Running);
            output.Progress($"runner {runnerId} up; {supervisor.All().Count} instance(s), " +
                            $"{adopted} already running");

            var contentHttp = LauncherHttp.Create();
            var dispatcher = new CommandDispatcher(supervisor, new BuildStore(paths),
                new ContentFetcher(paths, contentHttp), config.ContentBaseUrl);
            var links = new List<RunnerLink>();

            // The owner's own control plane. Outbound even though it is usually on this same box.
            if (config.WebServerUrl is not null)
            {
                var local = new RunnerLink(config.WebServerUrl, runnerId, dispatcher, supervisor,
                    ControlOrigin.Local);
                links.Add(local);
                _ = local.RunAsync(ct);
                output.Progress($"control plane: {config.WebServerUrl}");
            }

            // The orchestrator, if this box has been adopted. Commands from here arrive with
            // ControlOrigin.Orchestrator, which is what the supervisor arbitrates control mode on.
            if (config.ConductorControl && config.ConductorUrl is not null)
            {
                var conductor = new RunnerLink(config.ConductorUrl, runnerId, dispatcher, supervisor,
                    ControlOrigin.Orchestrator);
                links.Add(conductor);
                _ = conductor.RunAsync(ct);
                output.Progress($"offering control to {config.ConductorUrl}");
            }
            else if (config.ConductorControl)
            {
                output.Progress("conductor_control is set but no conductor url is configured");
            }

            // Control events go to every linked plane. The orchestrator needs them because they are
            // its alerts; the owner's own plane gets them so a release triggered from the CLI still
            // shows up in the dashboard they are looking at.
            supervisor.ControlEventSink = async (evt, token) =>
            {
                var acks = await Task.WhenAll(links.Select(l => l.SendControlEventAsync(evt, token)));
                return acks.Any(a => a);
            };

            // Autostart anything with an always policy that is not already up.
            foreach (var instance in supervisor.All())
            {
                if (instance.State == InstanceState.Running)
                    continue;
                if (instance.Spec.RestartPolicy != Launcher.Protocol.RestartPolicy.Always)
                    continue;
                try
                {
                    await supervisor.StartAsync(instance.Name, ControlOrigin.Local, ct);
                }
                catch (Exception ex)
                {
                    output.Progress($"{instance.Name}: autostart failed: {ex.Message}");
                }
            }

            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }

            output.Progress("runner shutting down; leaving instances running");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Status(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("status", "show runner identity, link state and instances");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var runnerId = RunnerIdentity.LoadOrCreate(paths);
            var configStore = new RunnerConfigStore(paths);
            var config = configStore.Load();
            var store = new InstanceStore(paths);

            var payload = new
            {
                runner_id = runnerId,
                data_root = paths.Root,
                web_server_url = config.WebServerUrl,
                conductor_control = config.ConductorControl,
                conductor_url = config.ConductorUrl,
                control_key_fingerprint = config.ControlKeyFingerprint,
                granted_scopes = config.GrantedScopes,
                instances = store.Names(),
            };

            if (output.IsJson)
                return output.Ok(payload);

            output.Line($"runner id     : {runnerId}");
            output.Line($"data root     : {paths.Root}");
            output.Line($"control plane : {config.WebServerUrl ?? "(none)"}");
            output.Line($"conductor     : " + (config.ConductorControl
                ? $"{config.ConductorUrl ?? "(url unset)"} [offering control]"
                : "(not offering control)"));
            if (config.ControlKeyFingerprint is not null)
                output.Line($"key           : {config.ControlKeyFingerprint[..16]}...");
            output.Line($"instances     : {store.Names().Count}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command Link(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var url = new Argument<string>("conductor-url")
            { Description = "for example https://conductor.vortexfps.org" };

        var command = new Command("link", "offer this box for official orchestration");
        command.Arguments.Add(url);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var configStore = new RunnerConfigStore(paths);

            var fingerprint = configStore.EnsureKeyPair();
            var config = configStore.Load() with
            {
                ConductorControl = true,
                ConductorUrl = parse.GetValue(url),
                ControlKeyFingerprint = fingerprint,
            };
            configStore.Save(config);

            return output.Ok(
                new { conductor_url = config.ConductorUrl, control_key_fingerprint = fingerprint },
                $"""
                 offering control to {config.ConductorUrl}
                 fingerprint: {fingerprint}

                 Servers on this box now announce available_for_control, which puts an offer in the
                 adoption queue. Nothing is granted until a Conductor operator accepts and this runner
                 completes the handshake with its key. Undo at any time with `vortex runner unlink`.
                 """);
        });

        return command;
    }

    private static Command Unlink(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var keepKey = new Option<bool>("--keep-key")
            { Description = "keep the identity keypair so a future link reuses the same fingerprint" };

        var command = new Command("unlink", "stop offering this box, and revoke any existing grant");
        command.Options.Add(keepKey);

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var configStore = new RunnerConfigStore(paths);

            var config = configStore.Load() with
            {
                ConductorControl = false,
                ConductorUrl = null,
                GrantedScopes = null,
            };
            configStore.Save(config);

            if (!parse.GetValue(keepKey))
                configStore.DeleteKeyPair();

            // Instances still marked orchestrated are returned individually, through the release path,
            // so each one raises its own alert with its own player count. Silently flipping them here
            // would be the one case where an orchestrator loses a server with no explanation.
            var store = new InstanceStore(paths);
            var stillOrchestrated = store.List()
                .Where(s => s.ControlMode == Launcher.Protocol.ControlMode.Orchestrated)
                .Select(s => s.Name)
                .ToList();

            return output.Ok(new { unlinked = true, still_orchestrated = stillOrchestrated },
                stillOrchestrated.Count == 0
                    ? "unlinked"
                    : "unlinked. Still under orchestrator control: " +
                      string.Join(", ", stillOrchestrated) +
                      $"{Environment.NewLine}Release each with `vortex server release <name>` so the " +
                      "orchestrator gets an alert with the player count.");
        });

        return command;
    }

    private static Command RotateKey(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("rotate-key", "replace this box's orchestration identity key");

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var runnerId = RunnerIdentity.LoadOrCreate(paths);
            var configStore = new RunnerConfigStore(paths);
            var config = configStore.Load();

            if (config.ConductorUrl is null)
                return output.Fail("not_linked",
                    "this runner is not linked; `vortex runner link` creates the first key",
                    ExitCodes.Conflict);

            // The new key is signed by the old one, which is what proves continuity. The old key
            // stays this runner's identity until the plane has accepted the new one, so a rotation
            // that fails partway leaves the box able to authenticate with what it had.
            RunnerConfigStore.RotationRequest rotation;
            try
            {
                rotation = configStore.BeginRotation();
            }
            catch (InvalidOperationException ex)
            {
                return output.Fail("no_key", ex.Message, ExitCodes.Conflict);
            }

            try
            {
                using var http = LauncherHttp.Create();
                var url = $"{config.ConductorUrl.TrimEnd('/')}" +
                          $"/api/v1/adoption/runners/{runnerId}/rotate-key";

                using var response = await http.PostAsync(url, new StringContent(
                    ManagementProtocol.Serialize(new
                    {
                        new_public_key_pem = rotation.NewPublicKeyPem,
                        signature_by_current_key = rotation.SignatureByCurrentKey,
                    }), System.Text.Encoding.UTF8, "application/json"), ct);

                if (!response.IsSuccessStatusCode)
                {
                    configStore.AbandonRotation();
                    return output.Fail("rotation_refused",
                        $"{config.ConductorUrl} refused the new key " +
                        $"({(int)response.StatusCode}); the existing key is unchanged",
                        ExitCodes.Error);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                configStore.AbandonRotation();
                return output.Fail("conductor_unreachable",
                    $"could not reach {config.ConductorUrl} ({ex.Message}); " +
                    "the existing key is unchanged", ExitCodes.Unavailable);
            }

            configStore.CommitRotation();
            configStore.Save(config with { ControlKeyFingerprint = rotation.NewFingerprint });

            return output.Ok(new { control_key_fingerprint = rotation.NewFingerprint },
                $"""
                 rotated. new fingerprint: {rotation.NewFingerprint}

                 Running servers keep announcing the old fingerprint until they restart. The
                 orchestrator already knows both, so nothing is interrupted.
                 """);
        });

        return command;
    }

    private static Command InstallService(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("install-service",
            "print the service definition for this platform");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var exe = Environment.ProcessPath ?? "vortex";

            // KillMode=process is the whole point: stopping the runner must not stop the game servers
            // it supervises, because the next runner adopts them from their pidfiles.
            var unit = $"""
                [Unit]
                Description=Vortex Arena server runner
                After=network-online.target

                [Service]
                Type=simple
                ExecStart={exe} runner run --data-root {paths.Root}
                Restart=always
                RestartSec=5
                KillMode=process

                [Install]
                WantedBy=multi-user.target
                """;

            var windows = $"""
                sc.exe create VortexRunner binPath= "{exe} runner run --data-root {paths.Root}" start= auto
                sc.exe description VortexRunner "Vortex Arena server runner"
                """;

            if (output.IsJson)
                return output.Ok(new { systemd = unit, windows });

            output.Line(OperatingSystem.IsWindows() ? windows : unit);
            return ExitCodes.Ok;
        });

        return command;
    }
}
