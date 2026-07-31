using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Core.Metrics;
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
        runner.Subcommands.Add(NewToken(jsonOption, rootOption));
        runner.Subcommands.Add(InstallService(jsonOption, rootOption));
        root.Subcommands.Add(runner);
    }

    private static Command Run(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("run", "run the supervisor in the foreground");

        var metricsPort = new Option<int?>("--metrics-port")
        {
            Description = "port for the Prometheus scrape endpoint, or 0 to run none",
        };
        var metricsBind = new Option<string?>("--metrics-bind")
        {
            Description = "address the scrape endpoint binds to (default 127.0.0.1)",
        };
        command.Options.Add(metricsPort);
        command.Options.Add(metricsBind);

        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var runnerId = RunnerIdentity.LoadOrCreate(paths);
            var config = new RunnerConfigStore(paths).Load();
            var startedAt = DateTimeOffset.UtcNow;

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

            // On the runner rather than on the WebServer, because the runner is what has the numbers
            // and the WebServer may not be running at all: a box under pure Conductor control never
            // starts one, and a box under local control can have it stopped for an upgrade while forty
            // players are still connected. Metrics that disappear whenever the dashboard does are
            // metrics nobody can alert on.
            using var metrics = StartMetrics(output, config, supervisor, runnerId, startedAt, paths,
                parse.GetValue(metricsPort), parse.GetValue(metricsBind), ct);

            // One expression decides both whether this box dials a Conductor and what its status
            // reports, so the snapshot cannot name a plane the runner never linked to. Both halves
            // have to be true: an address with the opt-in switched off is a box that offers nothing.
            var conductorUrl = config.ConductorControl ? config.ConductorUrl : null;

            var contentHttp = LauncherHttp.Create();
            var dispatcher = new CommandDispatcher(supervisor, new BuildStore(paths),
                new ContentFetcher(paths, contentHttp), config.ContentBaseUrl, conductorUrl);
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
            if (conductorUrl is not null)
            {
                var conductor = new RunnerLink(conductorUrl, runnerId, dispatcher, supervisor,
                    ControlOrigin.Orchestrator);
                links.Add(conductor);
                _ = conductor.RunAsync(ct);
                output.Progress($"offering control to {conductorUrl}");
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

    /// <summary>Bind the scrape endpoint, or explain why there is none and carry on.
    ///
    /// Nothing here is allowed to be fatal. A port already taken, an address that does not parse, a
    /// container with no permission to bind: every one of those is a reason to have no metrics, and
    /// none of them is a reason to stop supervising game servers. A runner that refuses to start
    /// because a monitoring endpoint could not bind has turned an observability feature into an
    /// outage.</summary>
    private static MetricsEndpoint? StartMetrics(Output output, RunnerConfig config,
        InstanceSupervisor supervisor, string runnerId, DateTimeOffset startedAt, LauncherPaths paths,
        int? portOverride, string? bindOverride, CancellationToken ct)
    {
        var port = portOverride ?? config.MetricsPort;
        if (port <= 0)
        {
            output.Progress("metrics: disabled");
            return null;
        }

        var bindText = bindOverride ?? config.MetricsBindAddress;
        if (!IPAddress.TryParse(bindText, out var bind))
        {
            output.Progress($"metrics: '{bindText}' is not an address; no endpoint");
            return null;
        }

        var endpoint = new MetricsEndpoint(bind, port,
            () => RunnerMetrics.Render(supervisor, config, runnerId, startedAt, paths.Root));

        try
        {
            endpoint.Start(ct);
        }
        catch (SocketException ex)
        {
            endpoint.Dispose();
            output.Progress($"metrics: could not bind {bindText}:{port} ({ex.SocketErrorCode}); " +
                            "no endpoint. Pick another with --metrics-port.");
            return null;
        }

        output.Progress($"metrics: http://{bind}:{port}/metrics");
        return endpoint;
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
                web_token_prefix = config.WebToken?.Prefix,
                metrics_url = MetricsUrl(config),
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
            // The prefix, never the token. Its absence is the answer to "why does the panel 401?".
            output.Line($"panel token   : " + (config.WebToken is { } t
                ? $"{t.Prefix}... (issued {t.IssuedAt:yyyy-MM-dd})"
                : "(none; run `vortex runner new-token`)"));
            output.Line($"metrics       : {MetricsUrl(config) ?? "(disabled)"}");
            output.Line($"instances     : {store.Names().Count}");
            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>What the running daemon would expose, from config alone. `runner status` is a separate
    /// process from `runner run` and cannot ask it anything, so this is the configured endpoint rather
    /// than a live one; --metrics-port on the daemon's command line overrides it and is not visible
    /// here.</summary>
    private static string? MetricsUrl(RunnerConfig config) =>
        config.MetricsPort > 0
            ? $"http://{config.MetricsBindAddress}:{config.MetricsPort}/metrics"
            : null;

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

    private static Command NewToken(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("new-token",
            "mint a new control plane token, invalidating the current one");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var configStore = new RunnerConfigStore(paths);

            var previous = configStore.Load().WebToken?.Prefix;
            var token = configStore.IssueWebToken();

            // No overlap window, unlike a Conductor API key. There is exactly one of these per box and
            // one operator holding it, so "both work for an hour" buys nothing and leaves a live
            // credential the operator believes they have already replaced.
            return output.Ok(new { web_token = token, replaced_prefix = previous },
                $"""
                 {TokenBanner(token)}

                 {(previous is null
                     ? "This runner had no token; the panel was rejecting every request."
                     : $"The previous token ({previous}...) stopped working just now. Anything using " +
                       "it, including an open panel tab, has to be given the new one.")}
                 The control plane picks this up without a restart.
                 """);
        });

        return command;
    }

    /// <summary>The one place the token is ever rendered. Loud on purpose: this is the only moment it
    /// exists outside the operator's clipboard, because only its hash is stored.</summary>
    private static string TokenBanner(string token) =>
        $"""
         control plane token: {token}

         !! Copy it now. It is stored hashed and will NOT be shown again. If it is lost, run
         !! `vortex runner new-token` to mint another; there is no way to recover this one.
         """;

    private static Command InstallService(Option<bool> jsonOption, Option<string?> rootOption)
    {
        var command = new Command("install-service",
            "print the service definition for this platform");

        command.SetAction(parse =>
        {
            var output = new Output(parse.GetValue(jsonOption));
            var paths = new LauncherPaths(parse.GetValue(rootOption));
            var exe = Environment.ProcessPath ?? "vortex";

            // A box being set up as a service is the first moment there is an operator present to hand
            // a credential to, which is why the token is minted here rather than on first boot of the
            // web server, where nobody would be watching the output. Only when there is none already:
            // re-running this to regenerate a unit file must not lock the operator out of their panel.
            var token = new RunnerConfigStore(paths).EnsureWebToken();

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
                return output.Ok(new { systemd = unit, windows, web_token = token });

            output.Line(OperatingSystem.IsWindows() ? windows : unit);

            // The banner goes to stderr, where Output.Progress puts it, so that
            // `vortex runner install-service > vortex.service` writes a unit file and not a unit file
            // with a live credential in it. It is still on the operator's terminal either way.
            if (token is not null)
                output.Progress(Environment.NewLine + TokenBanner(token));

            return ExitCodes.Ok;
        });

        return command;
    }
}
