using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Launcher.Protocol;

namespace Launcher.WebServer;

public sealed class WebServerOptions
{
    /// <summary>Bearer token for every request, including the WebSocket upgrade. Generated at
    /// `vortex runner install-service` and stored hashed.</summary>
    public string? Token { get; set; }

    /// <summary>Binding beyond loopback requires this to be set deliberately, and either TLS or a
    /// documented reverse proxy in front. The default is the safe one because the unsafe one is a
    /// panel with a bearer token on a public interface.</summary>
    public bool AllowRemoteBinding { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class LinkedRunner(string runnerId, WebSocket socket)
{
    public string RunnerId { get; } = runnerId;
    public WebSocket Socket { get; } = socket;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public RunnerStatus? Status { get; set; }
    public SemaphoreSlim SendGate { get; } = new(1, 1);
}

/// <summary>Connected runners and the command path to them.
///
/// Runners dial out, including when the plane is on the same box. Making the local case the one
/// inbound exception would produce two auth models and two reconnect stories for no gain, and it is
/// exactly the shortcut that later turns into "the panel writes the file directly when it is
/// local".</summary>
public sealed class RunnerRegistry(WebServerOptions options, ILogger<RunnerRegistry> log)
{
    private readonly ConcurrentDictionary<string, LinkedRunner> _runners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Action<LogLine>>> _logSubscribers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<LinkedRunner> All => _runners.Values.ToList();

    public LinkedRunner? Find(string runnerId) =>
        _runners.TryGetValue(runnerId, out var runner) ? runner : null;

    /// <summary>The common case is one runner on this box, so the API lets callers omit the id rather
    /// than making every URL carry something an operator does not have.</summary>
    public LinkedRunner? Default => _runners.Values.FirstOrDefault();

    public void Register(LinkedRunner runner)
    {
        _runners[runner.RunnerId] = runner;
        log.LogInformation("runner {RunnerId} linked", runner.RunnerId);
    }

    public void Unregister(string runnerId)
    {
        _runners.TryRemove(runnerId, out _);
        log.LogInformation("runner {RunnerId} disconnected", runnerId);
    }

    public void Complete(CommandResult result)
    {
        if (_pending.TryRemove(result.CommandId, out var waiter))
            waiter.TrySetResult(result);
    }

    public void Publish(LogLine line)
    {
        if (!_logSubscribers.TryGetValue(line.InstanceName, out var subscribers))
            return;
        lock (subscribers)
            foreach (var subscriber in subscribers.ToList())
                subscriber(line);
    }

    public IDisposable SubscribeLogs(string instance, Action<LogLine> handler)
    {
        var list = _logSubscribers.GetOrAdd(instance, _ => []);
        lock (list)
            list.Add(handler);
        return new Subscription(() =>
        {
            lock (list)
                list.Remove(handler);
        });
    }

    public async Task<CommandResult> SendAsync(LinkedRunner runner, string method, string path,
        string? body, string actor, CancellationToken ct)
    {
        var command = new CommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("n"),
            Method = method,
            Path = path,
            Body = body,
            ActorId = actor,
        };

        var waiter = new TaskCompletionSource<CommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[command.CommandId] = waiter;

        await SendFrameAsync(runner, new PlaneFrame
        {
            Kind = PlaneFrameKind.Command,
            Command = command,
        }, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
        await using var registration = timeout.Token.Register(() =>
            waiter.TrySetResult(new CommandResult
            {
                CommandId = command.CommandId,
                Status = StatusCodes.Status504GatewayTimeout,
                Body = ManagementProtocol.Serialize(
                    ApiError.Of("runner_timeout", "the runner did not answer in time")),
            }));

        try
        {
            return await waiter.Task;
        }
        finally
        {
            _pending.TryRemove(command.CommandId, out _);
        }
    }

    public async Task SendFrameAsync(LinkedRunner runner, PlaneFrame frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(ManagementProtocol.Serialize(frame));
        await runner.SendGate.WaitAsync(ct);
        try
        {
            await runner.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException ex)
        {
            log.LogDebug(ex, "send to {RunnerId} failed", runner.RunnerId);
        }
        finally
        {
            runner.SendGate.Release();
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
