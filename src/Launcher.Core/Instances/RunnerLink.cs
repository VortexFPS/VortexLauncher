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
    ContentFetcher? content = null, string? contentBaseUrl = null)
{
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
        var segments = path.Trim('/').Split('/');

        if (segments is ["agent" or "runner", "status"])
            return (ProtocolStatus.Ok, RunnerSnapshot());

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
                return (ProtocolStatus.Ok, instance.Tail(500));

            case ("audit", _):
                return (ProtocolStatus.Ok, ReadAudit(name));

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

    private RunnerStatus RunnerSnapshot() => new()
    {
        RunnerId = RunnerIdentity.Current,
        Version = typeof(CommandDispatcher).Assembly.GetName().Version?.ToString(3) ?? "dev",
        Platform = PlatformKey.Current,
        Hostname = Environment.MachineName,
        StartedAt = DateTimeOffset.UtcNow,
        Instances = supervisor.All().Select(i => i.Status()).ToList(),
    };

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
                await SendAsync(new RunnerFrame
                {
                    Kind = RunnerFrameKind.Status,
                    RunnerId = runnerId,
                    Status = new RunnerStatus
                    {
                        RunnerId = runnerId,
                        Version = typeof(RunnerLink).Assembly.GetName().Version?.ToString(3) ?? "dev",
                        Platform = PlatformKey.Current,
                        Hostname = Environment.MachineName,
                        StartedAt = DateTimeOffset.UtcNow,
                        Instances = supervisor.All().Select(i => i.Status()).ToList(),
                    },
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
