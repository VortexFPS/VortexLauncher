using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Launcher.Protocol;

namespace Launcher.WebServer;

public sealed class WebServerOptions
{
    /// <summary>Where the runner keeps runner.json, which holds the hash of the bearer token every
    /// request must carry. Null means the well-known per-user location, which is right whenever the
    /// runner was not pointed elsewhere with `--data-root`.
    ///
    /// There is deliberately no plaintext token setting. One existed, nothing ever wrote it, and a
    /// panel that rejects everything until somebody hand-edits a config file is not a credential
    /// model. See <see cref="RunnerTokenStore"/> for why the hash lives in the runner's file rather
    /// than a copy of its own here.</summary>
    public string? RunnerConfigPath { get; set; }

    /// <summary>Binding beyond loopback requires this to be set deliberately, and either TLS or a
    /// documented reverse proxy in front. The default is the safe one because the unsafe one is a
    /// panel with a bearer token on a public interface. On its own this setting is not enough:
    /// <see cref="BindingGate"/> refuses to start unless <see cref="CertificatePath"/> or
    /// <see cref="BehindReverseProxy"/> comes with it.</summary>
    public bool AllowRemoteBinding { get; set; }

    /// <summary>PKCS#12 (.pfx) file this process serves HTTPS from. One of the two ways to satisfy
    /// <see cref="AllowRemoteBinding"/>, and the one that does not depend on anything outside this
    /// process being configured correctly.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password for <see cref="CertificatePath"/>, where the file has one.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>States that a reverse proxy in front of this process terminates TLS. It is an
    /// acknowledgement rather than a feature: nothing here can check that a proxy exists, so what the
    /// setting buys is that somebody had to assert it on purpose before the API left loopback.</summary>
    public bool BehindReverseProxy { get; set; }

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

    /// <summary>Instance name -> how many viewers are watching it. A runner forwards log lines only
    /// for instances a plane asked for, so this is what decides when to ask and when to stop.</summary>
    private readonly Dictionary<string, int> _watchers = new(StringComparer.Ordinal);

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

    /// <summary>Watch one instance's log stream, and keep the runner forwarding it for as long as
    /// anybody is.
    ///
    /// Reference counted rather than one subscribe per viewer and one unsubscribe per close. Two
    /// operators on the same server is an ordinary thing — the on-call one and whoever is being asked
    /// about it — and an unsubscribe sent when either of them closes a tab stops the lines arriving for
    /// the other, with nothing on their screen to say so. It reads as a dead console, and the fix looks
    /// like reloading the page, which is how it stays unreported.
    ///
    /// The count and the frame are decided under the same lock so two viewers arriving at once cannot
    /// both skip the subscribe, and the frame itself is sent outside it: a socket write must not be
    /// able to hold the count of every instance on the box.</summary>
    public async Task<IAsyncDisposable> WatchLogsAsync(LinkedRunner runner, string instance,
        Action<LogLine> handler, CancellationToken ct)
    {
        var subscription = SubscribeLogs(instance, handler);

        bool first;
        lock (_watchers)
        {
            _watchers.TryGetValue(instance, out var count);
            _watchers[instance] = count + 1;
            first = count == 0;
        }

        if (first)
            await SendFrameAsync(runner, new PlaneFrame
            {
                Kind = PlaneFrameKind.Subscribe,
                InstanceName = instance,
            }, ct);

        return new AsyncSubscription(async () =>
        {
            subscription.Dispose();

            bool last;
            lock (_watchers)
            {
                var count = _watchers.TryGetValue(instance, out var current) ? current - 1 : 0;
                last = count <= 0;
                if (last)
                    _watchers.Remove(instance);
                else
                    _watchers[instance] = count;
            }

            if (!last)
                return;

            // Not the caller's token: this runs while a socket is being torn down, and the usual
            // reason for that is the token that was cancelled.
            await SendFrameAsync(runner, new PlaneFrame
            {
                Kind = PlaneFrameKind.Unsubscribe,
                InstanceName = instance,
            }, CancellationToken.None);
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

    private sealed class AsyncSubscription(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
