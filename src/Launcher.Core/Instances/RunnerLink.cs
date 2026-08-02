using System.Net.WebSockets;
using System.Text;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>Executes a command envelope against the local supervisor.
///
/// This is the runner's API, implemented once. The WebServer and Conductor both reach it through the
/// same envelope, which is what makes them peers rather than two management implementations, and it
/// is why adding a verb here gives both planes the feature with no protocol change.</summary>
public sealed class CommandDispatcher(
    InstanceSupervisor supervisor, BuildStore builds,
    ContentFetcher? content = null, string? contentBaseUrl = null, string? conductorUrl = null,
    LauncherPaths? paths = null, SourceBuildJobs? sourceBuilds = null)
{
    /// <summary>The source routes need a root to read specs and checkouts out of. Optional in the same
    /// way <paramref name="content"/> is: a dispatcher built without one still serves every other verb,
    /// and the source routes answer 404 rather than the process failing to start.</summary>
    private readonly SourceBuildJobs? _sourceBuilds =
        sourceBuilds ?? (paths is null ? null : new SourceBuildJobs(paths));

    private SourceStore? Sources => paths is null ? null : new SourceStore(paths);

    public async Task<CommandResult> ExecuteAsync(CommandEnvelope command, ControlOrigin origin,
        CancellationToken ct)
    {
        try
        {
            var (status, body) = await RouteAsync(command, origin, ct);
            return new CommandResult
            {
                CommandId = command.CommandId,
                Status = status,
                Body = body is null ? null : ManagementProtocol.Serialize(body),
            };
        }
        catch (InstanceOrchestratedException ex)
        {
            // 409 with the controlling plane and both exits attached, so a UI can render the banner
            // from the error itself rather than special-casing every endpoint that produces one.
            return Error(command, ProtocolStatus.Conflict, new ApiError
            {
                Code = ApiErrorCodes.InstanceOrchestrated,
                Message = ex.Message,
                Orchestrated = ex.Detail,
            });
        }
        catch (KeyNotFoundException ex)
        {
            return Error(command, ProtocolStatus.NotFound,
                ApiError.Of(ApiErrorCodes.InstanceNotFound, ex.Message));
        }
        catch (InstanceExistsException ex)
        {
            // 409 rather than the generic 400 below, because a panel has to be able to tell a taken
            // name from a malformed spec: the first keeps everything the operator typed on screen with
            // one field to fix, the second is a bug in whatever built the request.
            return Error(command, ProtocolStatus.Conflict,
                ApiError.Of(ApiErrorCodes.InstanceExists, ex.Message));
        }
        catch (Exception ex)
        {
            return Error(command, ProtocolStatus.BadRequest,
                ApiError.Of(ApiErrorCodes.InvalidRequest, ex.Message));
        }
    }

    private async Task<(int Status, object? Body)> RouteAsync(CommandEnvelope command,
        ControlOrigin origin, CancellationToken ct)
    {
        var path = command.Path.StartsWith(ManagementProtocol.ApiPrefix, StringComparison.Ordinal)
            ? command.Path[ManagementProtocol.ApiPrefix.Length..]
            : command.Path;

        // The envelope carries the whole request line, query string included, because a plane tunnels
        // what it received rather than a parsed form of it. Routing is on the path alone; the
        // parameters are read where they are used.
        var mark = path.IndexOf('?');
        if (mark >= 0)
            path = path[..mark];

        var segments = path.Trim('/').Split('/');

        if (segments is ["agent" or "runner", "status"])
            return (ProtocolStatus.Ok, Snapshot());

        if (segments is ["builds"])
            return (ProtocolStatus.Ok, builds.List().Select(b => new BuildSummary
            {
                Id = b.Id,
                Version = b.Version,
                Provider = b.Provider,
                PlatformKey = b.PlatformKey,
                Layout = b.Layout,
                SizeBytes = builds.SizeOf(b),
                InstalledAt = b.InstalledAt,
            }).ToList());

        if (segments is ["content"])
            return (ProtocolStatus.Ok, CachedContent());

        if (segments is ["sources", ..])
            return SourceRoute(command, origin, segments);

        if (segments is ["instances"])
        {
            if (command.Method == ProtocolMethods.Get)
                return (ProtocolStatus.Ok,
                    supervisor.All().Select(i => i.Status()).ToList());

            var spec = Body<InstanceSpec>(command)
                ?? throw new ArgumentException("an instance spec is required");
            supervisor.Create(spec);
            return (ProtocolStatus.Created, spec);
        }

        if (segments is ["instances", var name, ..])
        {
            var action = segments.Length > 2 ? segments[2] : null;
            return await InstanceRouteAsync(command, origin, name, action, ct);
        }

        return (ProtocolStatus.NotFound,
            ApiError.Of(ApiErrorCodes.InvalidRequest, $"no route for {command.Method} {command.Path}"));
    }

    private async Task<(int Status, object? Body)> InstanceRouteAsync(CommandEnvelope command,
        ControlOrigin origin, string name, string? action, CancellationToken ct)
    {
        var instance = supervisor.Require(name);

        switch (action, command.Method)
        {
            case (null, "GET"):
                return (ProtocolStatus.Ok, instance.Spec);

            case ("status", _):
                await instance.ProbeAsync(ct);
                return (ProtocolStatus.Ok, instance.Status());

            case ("logs", _):
                return (ProtocolStatus.Ok, instance.Tail(TailLines(command.Path)));

            case ("audit", _):
                return (ProtocolStatus.Ok, ReadAudit(name));

            // server.cfg. A read in either mode, because the file is on the owner's own disk and
            // being orchestrated hides nothing from them.
            case ("config", "GET"):
                return (ProtocolStatus.Ok, new ConfigDocument(supervisor.Store.LoadConfig(name)));

            case ("config", "PATCH"):
                // Absent text is refused rather than treated as an empty file: the two are
                // indistinguishable once written, and one of them silently deletes the operator's
                // config because a caller spelled the field wrong.
                if (Body<ConfigDocument>(command) is not { Text: not null } document)
                    throw new ArgumentException(
                        """a server.cfg body is required, as {"text": "..."}""");

                supervisor.WriteConfig(name, document.Text, origin);
                return (ProtocolStatus.Ok, document);

            case (null, "PATCH"):
                var patch = Body<InstanceSpec>(command)
                    ?? throw new ArgumentException("an instance spec is required");
                supervisor.UpdateSpec(patch with { Name = name }, origin);

                // Content is fetched, never received. The plane named a set of hashes; going and
                // getting them is this side's job, and a failure here must not leave the instance
                // half applied, so the spec keeps its previous set until every package lands.
                if (patch.ContentSet is { Count: > 0 } wanted && content is not null)
                {
                    var sync = await content.SyncAsync(
                        supervisor.Require(name).Spec, wanted, contentBaseUrl ?? "", ct);
                    if (!sync.Ok)
                        return (ProtocolStatus.BadRequest, ApiError.Of(
                            ApiErrorCodes.ContentInvalid,
                            "content sync failed: " + string.Join("; ",
                                sync.Failed.Select(f => $"{f.Key[..12]}: {f.Value}"))));

                    content.Gc(supervisor.Store);
                }

                return (ProtocolStatus.Ok, supervisor.Require(name).Spec);

            case ("content", _):
                return (ProtocolStatus.Ok, new
                {
                    content_set = instance.Spec.ContentSet ?? [],
                    cache_dir = content?.CacheDir,
                });

            case (null, "DELETE"):
                supervisor.Delete(name, origin);
                return (ProtocolStatus.NoContent, null);

            case ("start", _):
                await supervisor.StartAsync(name, origin, ct);
                return (ProtocolStatus.Ok, instance.Status());

            case ("restart", _):
                await supervisor.RestartAsync(name, origin, ct);
                return (ProtocolStatus.Ok, instance.Status());

            case ("stop", _):
                await supervisor.StopAsync(name, origin, command.ActorId, ct: ct);
                return (ProtocolStatus.Ok, instance.Status());

            case ("drain", _):
                await supervisor.DrainAsync(name, Body<DrainRequest>(command) ?? new DrainRequest(),
                    origin, ct);
                return (ProtocolStatus.Ok, instance.Status());

            case ("exec", _):
                var exec = Body<ExecRequest>(command)
                    ?? throw new ArgumentException("a command is required");
                await supervisor.SendAsync(name, exec.Command, origin, ct);
                return (ProtocolStatus.Ok, new { sent = exec.Command });

            // Always available to the host owner, whatever the mode. This is the one route that is
            // deliberately not gated on control mode, because the exits are the owner's alone.
            case ("release", _):
                await supervisor.ReleaseAsync(name,
                    Body<ReleaseRequest>(command) ?? new ReleaseRequest(), command.ActorId, ct);
                return (ProtocolStatus.Ok, supervisor.Require(name).Spec);

            default:
                return (ProtocolStatus.NotFound, ApiError.Of(
                    ApiErrorCodes.InvalidRequest, $"no route for {command.Method} {command.Path}"));
        }
    }

    /// <summary>Building the game from a git checkout, rather than installing a published release.
    ///
    /// <para><b>Everything that mutates is local-origin only, and this is the important line in the
    /// file.</b> A source build clones a repository the CALLER names and compiles it here. Exposed to an
    /// orchestrator that would be arbitrary code execution addressed by URL, on a box a community
    /// operator lent to a network — categorically different from the instance routes, which only ever
    /// start a binary the owner already installed and only ever from the build store. Reads stay open to
    /// both planes: what a box could build is no more sensitive than the builds and status it already
    /// publishes, and an orchestrator being able to see that a build is running is what stops it
    /// treating a busy box as an idle one.</para>
    ///
    /// <para>The build itself is a job rather than a call, because it runs for tens of minutes and every
    /// other verb here answers inside a 30-second envelope. See <see cref="SourceBuildJobs"/>.</para></summary>
    private (int Status, object? Body) SourceRoute(CommandEnvelope command, ControlOrigin origin,
        string[] segments)
    {
        if (Sources is not { } store || _sourceBuilds is null)
            return (ProtocolStatus.NotFound, ApiError.Of(ApiErrorCodes.InvalidRequest,
                "this runner was started without an install root, so it serves no source routes"));

        if (command.Method != ProtocolMethods.Get && origin != ControlOrigin.Local)
            return (ProtocolStatus.Forbidden, ApiError.Of(ApiErrorCodes.ScopeDenied,
                "source builds are the host owner's alone. This call compiles a repository named in " +
                "the request, so it is only accepted from the local plane — an orchestrator can read " +
                "what this box has built and watch a running build, but cannot start one."));

        var name = segments.Length > 1 ? segments[1] : null;
        var action = segments.Length > 2 ? segments[2] : null;

        // ---- /sources ----------------------------------------------------------------------------
        if (name is null)
        {
            if (command.Method == ProtocolMethods.Get)
                return (ProtocolStatus.Ok, store.List().Select(Describe).ToList());

            // A request record rather than SourceSpec: the spec's Name and Repo are `required`, which
            // System.Text.Json enforces, so posting {"name","ref"} to change a ref would be rejected
            // for omitting a field this route is meant to leave alone.
            var request = Body<SourceRequest>(command)
                ?? throw new ArgumentException("a source spec is required");

            if (request.Name is not { Length: > 0 } requested)
                throw new ArgumentException("a source name is required");

            SourceStore.ValidateName(requested);

            // Update in place, matching `source set`: a call that named only a ref must not silently
            // reset the repo it was pointed at.
            var existing = store.Get(requested);
            var spec = new SourceSpec
            {
                Name = requested,
                Repo = string.IsNullOrWhiteSpace(request.Repo)
                    ? existing?.Repo ?? $"{LauncherConfig.RepoUrl}.git"
                    : request.Repo,
                Ref = string.IsNullOrWhiteSpace(request.Ref) ? existing?.Ref ?? "main" : request.Ref,
                Target = request.Target ?? existing?.Target,
                GodotPath = request.Godot ?? existing?.GodotPath,
                LastBuildId = existing?.LastBuildId,
                LastBuiltSha = existing?.LastBuiltSha,
                LastBuiltAt = existing?.LastBuiltAt,
            };

            store.Save(spec);
            return (existing is null ? ProtocolStatus.Created : ProtocolStatus.Ok, Describe(spec));
        }

        // ---- /sources/{name}/build ---------------------------------------------------------------
        if (action == "build")
        {
            if (command.Method == ProtocolMethods.Get)
                return _sourceBuilds.Current is { } running
                    ? (ProtocolStatus.Ok, running)
                    : (ProtocolStatus.NotFound, ApiError.Of(ApiErrorCodes.InvalidRequest,
                        "no source build has run on this box since the runner started"));

            if (segments.Length > 3 && segments[3] == "cancel")
                return _sourceBuilds.Cancel()
                    ? (ProtocolStatus.Ok, _sourceBuilds.Current)
                    : (ProtocolStatus.NotFound, ApiError.Of(ApiErrorCodes.InvalidRequest,
                        "no source build is running"));

            var spec = store.Get(name)
                ?? throw new KeyNotFoundException(
                    $"no source '{name}'; POST /sources creates one before it can be built");

            var options = Body<SourceBuildRequest>(command) ?? new SourceBuildRequest();
            var effective = spec with { Target = options.Target ?? spec.Target };

            try
            {
                // 202: the body describes a build that has started, not one that has finished. A plane
                // that treated this as 200-means-done would report success before the clone.
                return (ProtocolStatus.Accepted,
                    _sourceBuilds.Start(effective, options.FetchMaps, store));
            }
            catch (InvalidOperationException ex)
            {
                return (ProtocolStatus.Conflict, ApiError.Of(ApiErrorCodes.InvalidRequest, ex.Message));
            }
        }

        // ---- /sources/{name} ---------------------------------------------------------------------
        if (action is not null)
            return (ProtocolStatus.NotFound, ApiError.Of(
                ApiErrorCodes.InvalidRequest, $"no route for {command.Method} {command.Path}"));

        if (command.Method == ProtocolMethods.Delete)
            return store.Delete(name)
                ? (ProtocolStatus.NoContent, null)
                : (ProtocolStatus.NotFound, ApiError.Of(ApiErrorCodes.InvalidRequest,
                    $"no source '{name}'"));

        var found = store.Get(name)
            ?? throw new KeyNotFoundException($"no source '{name}'");

        // A short doctor budget, not the default two minutes: this answer has to be back inside the
        // command envelope's 30 seconds. On a warm checkout doctor takes a moment; on a cold one it is
        // skipped by timing out, which costs only the part of the report that was always optional.
        var provider = new SourceProvider(paths!, builds);
        var report = provider.Inspect(found, null, TimeSpan.FromSeconds(12));

        return (ProtocolStatus.Ok, new
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
            template_present = report.TemplatePresent,
            tools = report.Tools.Select(t => new { name = t.Name, ok = t.Ok, path = t.Path, problem = t.Problem }),
            ready = report.Ready,
            problems = report.Problems,
            vx = report.VxDoctor is null ? null : (object)new
            {
                ok = report.VxDoctor.Ok,
                unsupported_schema = report.VxDoctor.UnsupportedSchema,
                checks = report.VxDoctor.Checks.Select(c => new
                {
                    name = c.Name, status = c.Status, detail = c.Detail,
                    required = c.Required, fix = c.Fix,
                }),
            },
            last_build_id = report.LastBuildId,
            last_built_at = report.LastBuiltAt,
        });
    }

    /// <summary>A source spec as a plane sees it. The preset is resolved rather than left null, because
    /// null means "this platform's default" and the plane asking may not be on this platform.</summary>
    private static object Describe(SourceSpec spec) => new
    {
        name = spec.Name,
        repo = spec.Repo,
        @ref = spec.Ref,
        target = spec.Target ?? SourceProvider.DefaultPreset(),
        godot = spec.GodotPath,
        last_build_id = spec.LastBuildId,
        last_built_sha = spec.LastBuiltSha,
        last_built_at = spec.LastBuiltAt,
    };

    /// <summary>This runner and everything it owns.
    ///
    /// Public because the runner link's heartbeat sends the same thing: a plane's cached status and
    /// the one it gets from asking must not be two different shapes assembled in two places, which is
    /// exactly how conductor_url came to be set in neither.</summary>
    public RunnerStatus Snapshot() => new()
    {
        RunnerId = RunnerIdentity.Current,
        Version = typeof(CommandDispatcher).Assembly.GetName().Version?.ToString(3) ?? "dev",
        Platform = PlatformKey.Current,
        Hostname = Environment.MachineName,
        StartedAt = DateTimeOffset.UtcNow,

        // The Conductor this box actually dials, not the one it has an address for. A panel reads
        // this to name the controlling plane from status alone, and an address with the opt-in
        // switched off would name a plane that holds nothing.
        ConductorUrl = conductorUrl,

        Instances = supervisor.All().Select(i => i.Status()).ToList(),
    };

    /// <summary>How many lines /logs?tail=N answers with.
    ///
    /// Clamped rather than refused at both ends. Below one there is nothing to return, and above the
    /// ring there is nothing to return either — a plane asking for more than the runner keeps means
    /// all of it, and should not be able to size a list allocation from a URL.</summary>
    private static int TailLines(string path) =>
        Math.Clamp(IntQuery(path, "tail") ?? DefaultTail, 1, SupervisedInstance.LogRingLines);

    private const int DefaultTail = 500;

    /// <summary>One integer query parameter out of the envelope's path.
    ///
    /// Hand-read because Launcher.Core is BCL-only by rule and this is one number: pulling a web stack
    /// into the runner to parse it would be the wrong trade. A parameter that is present but not a
    /// number falls back to the default rather than failing the call, because a log read is not worth
    /// refusing over a typo in a query string.</summary>
    private static int? IntQuery(string path, string key)
    {
        var mark = path.IndexOf('?');
        if (mark < 0)
            return null;

        foreach (var pair in path[(mark + 1)..].Split('&'))
        {
            var split = pair.IndexOf('=');
            if (split < 0 || !pair.AsSpan(0, split).SequenceEqual(key))
                continue;
            return int.TryParse(pair[(split + 1)..], out var value) ? value : null;
        }

        return null;
    }

    /// <summary>What is in the shared content cache, by hash.
    ///
    /// Listing never opens an archive: the file name is the hash, which is the whole point of
    /// addressing packages by their content. A package's name, its maps and where it was fetched from
    /// are not recoverable from the cache, so they are left null rather than guessed — a plane that
    /// wants them asks the store it fetched from.</summary>
    private IReadOnlyList<ContentPackage> CachedContent()
    {
        if (content is null || !Directory.Exists(content.CacheDir))
            return [];

        return new DirectoryInfo(content.CacheDir)
            .EnumerateFiles("*.pk3", SearchOption.AllDirectories)
            .Select(f => new ContentPackage
            {
                Sha256 = Path.GetFileNameWithoutExtension(f.Name),
                Name = f.Name,
                SizeBytes = f.Length,
                AddedAt = f.CreationTimeUtc,
            })
            .OrderBy(p => p.Sha256, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> ReadAudit(string name)
    {
        var path = supervisor.Store.PathsFor(name).AuditPath;
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path).TakeLast(500).ToList() : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static T? Body<T>(CommandEnvelope command) where T : class =>
        command.Body is null ? null : ManagementProtocol.Deserialize<T>(command.Body);

    private static CommandResult Error(CommandEnvelope command, int status, ApiError error) => new()
    {
        CommandId = command.CommandId,
        Status = status,
        Body = ManagementProtocol.Serialize(error),
    };

    private sealed record ExecRequest(string Command);

    /// <summary>The body of POST /sources. Every field is optional, including the ones SourceSpec makes
    /// required, because this route updates in place: naming only a ref must change only the ref.</summary>
    private sealed record SourceRequest
    {
        public string? Name { get; init; }
        public string? Repo { get; init; }
        public string? Ref { get; init; }
        public string? Target { get; init; }
        public string? Godot { get; init; }
    }

    /// <summary>The body of POST /sources/{name}/build, overriding the stored spec for one run without
    /// rewriting it.</summary>
    private sealed record SourceBuildRequest
    {
        public string? Target { get; init; }

        /// <summary>Defaults true, matching the CLI where skipping is the opt-in: a build with no maps
        /// starts and then finds nothing to load, which reads as a broken game rather than a partial
        /// build, so it is not the thing to get by saying nothing.</summary>
        public bool FetchMaps { get; init; } = true;
    }

    /// <summary>The body of the server.cfg routes, in both directions. The whole file as one string:
    /// it is a config the operator edits by hand in a text box, and anything structured here would be
    /// the runner claiming to understand a file that belongs to the game.</summary>
    private sealed record ConfigDocument(string Text);
}

/// <summary>The runner's outbound connection to a control plane.
///
/// Outbound always, including to a plane on the same box. No inbound port on a host machine means NAT
/// is never a question and a community operator never opens anything, and it keeps one auth model and
/// one reconnect story instead of two.</summary>
public sealed class RunnerLink(
    string planeUrl, string runnerId, CommandDispatcher dispatcher, InstanceSupervisor supervisor,
    ControlOrigin origin, string? token = null)
{
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly HashSet<string> _logSubscriptions = new(StringComparer.Ordinal);

    public bool Connected => _socket?.State == WebSocketState.Open;

    /// <summary>Connect and stay connected. Reconnects with backoff forever, because a control plane
    /// being down is a normal condition and not a reason for the runner to stop supervising.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(ct);
                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                attempt++;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, attempt))));
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        if (token is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        var uri = new Uri(new Uri(planeUrl), ManagementProtocol.RunnerLinkPath);
        var wsUri = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
        }.Uri;

        await socket.ConnectAsync(wsUri, ct);
        _socket = socket;

        await SendAsync(new RunnerFrame
        {
            Kind = RunnerFrameKind.Hello,
            RunnerId = runnerId,
            Hello = new RunnerHello
            {
                RunnerId = runnerId,
                Version = typeof(RunnerLink).Assembly.GetName().Version?.ToString(3) ?? "dev",
                Platform = PlatformKey.Current,
                Hostname = Environment.MachineName,
                Instances = supervisor.All().Select(i => i.Name).ToList(),
            },
        }, ct);

        var heartbeat = HeartbeatAsync(ct);
        var logs = ForwardLogsAsync(ct);

        try
        {
            await PumpAsync(socket, ct);
        }
        finally
        {
            _socket = null;
            logs.Dispose();
            await heartbeat;
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var received = new List<byte>();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                received.AddRange(buffer.Take(result.Count));
            } while (!result.EndOfMessage);

            PlaneFrame? frame;
            try
            {
                frame = ManagementProtocol.Deserialize<PlaneFrame>(
                    Encoding.UTF8.GetString(received.ToArray()));
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (frame is null)
                continue;

            switch (frame.Kind)
            {
                case PlaneFrameKind.Command when frame.Command is not null:
                    var outcome = await dispatcher.ExecuteAsync(frame.Command, origin, ct);
                    await SendAsync(new RunnerFrame
                    {
                        Kind = RunnerFrameKind.CommandResult,
                        RunnerId = runnerId,
                        Result = outcome,
                    }, ct);
                    break;

                case PlaneFrameKind.Subscribe when frame.InstanceName is not null:
                    lock (_logSubscriptions)
                        _logSubscriptions.Add(frame.InstanceName);
                    break;

                case PlaneFrameKind.Unsubscribe when frame.InstanceName is not null:
                    lock (_logSubscriptions)
                        _logSubscriptions.Remove(frame.InstanceName);
                    break;
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (Connected && !ct.IsCancellationRequested)
        {
            try
            {
                // The dispatcher's own snapshot rather than a second one assembled here. A plane's
                // cached copy is what its panel reads on nearly every screen, so a field the two
                // shapes disagree on is a field that is null in the place people actually look. The id
                // is the link's, because this frame is the link asserting who is speaking.
                await SendAsync(new RunnerFrame
                {
                    Kind = RunnerFrameKind.Status,
                    RunnerId = runnerId,
                    Status = dispatcher.Snapshot() with { RunnerId = runnerId },
                }, ct);

                await Task.Delay(
                    TimeSpan.FromSeconds(ManagementProtocol.HeartbeatIntervalSeconds), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { return; }
        }
    }

    private IDisposable ForwardLogsAsync(CancellationToken ct)
    {
        void Handler(LogLine line)
        {
            bool wanted;
            lock (_logSubscriptions)
                wanted = _logSubscriptions.Contains(line.InstanceName);
            if (!wanted)
                return;

            _ = SendAsync(new RunnerFrame
            {
                Kind = RunnerFrameKind.LogLine,
                RunnerId = runnerId,
                Log = line,
            }, ct);
        }

        supervisor.LineWritten += Handler;
        return new Unsubscriber(() => supervisor.LineWritten -= Handler);
    }

    /// <summary>Send a control event and wait briefly for the ack. Wired into the supervisor as its
    /// <see cref="InstanceSupervisor.ControlEventSink"/>.</summary>
    public async Task<bool> SendControlEventAsync(ControlEvent evt, CancellationToken ct)
    {
        if (!Connected)
            return false;

        await SendAsync(new RunnerFrame
        {
            Kind = RunnerFrameKind.ControlEvent,
            RunnerId = runnerId,
            ControlEvent = evt,
        }, ct);

        // The caller already bounds this. Whether the ack arrives changes nothing about whether the
        // owner's action proceeds; it only decides what gets logged.
        return true;
    }

    private async Task SendAsync(RunnerFrame frame, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(ManagementProtocol.Serialize(frame)),
                WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException) { }
        finally
        {
            _sendGate.Release();
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
