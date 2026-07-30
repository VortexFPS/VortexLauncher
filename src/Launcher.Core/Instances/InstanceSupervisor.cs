using System.Collections.Concurrent;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>Raised when a mutating operation is attempted on an orchestrated instance. Carries the
/// detail a UI needs to render the banner and both exit buttons, so the caller does not have to
/// special-case every endpoint that can produce it.</summary>
public sealed class InstanceOrchestratedException(InstanceSpec spec)
    : InvalidOperationException(
        $"instance '{spec.Name}' is controlled by {spec.ControllerUrl ?? "an orchestrator"}; " +
        "return it to local control or stop it")
{
    public OrchestratedDetail Detail { get; } = new()
    {
        ControllerUrl = spec.ControllerUrl ?? "",
        ControlledSince = spec.ControlledSince,
        GrantedScopes = spec.GrantedScopes,
    };
}

/// <summary>Who is asking. The runner is the only arbiter of control mode, and it decides by origin
/// rather than by anything the request asserts about itself.</summary>
public enum ControlOrigin
{
    /// <summary>The host owner: the local CLI, or their own WebServer.</summary>
    Local,

    /// <summary>The linked Conductor, over the runner link.</summary>
    Orchestrator,
}

/// <summary>Owns every instance on the box: adoption, supervision, health, and the control-mode rule
/// that exactly one plane operates an instance at a time.</summary>
public sealed class InstanceSupervisor : IDisposable
{
    private readonly InstanceStore _store;
    private readonly BuildStore _builds;
    private readonly ConcurrentDictionary<string, SupervisedInstance> _instances = new(StringComparer.Ordinal);
    private CancellationTokenSource? _health;

    public InstanceSupervisor(InstanceStore store, BuildStore builds)
    {
        _store = store;
        _builds = builds;
    }

    public InstanceStore Store => _store;

    /// <summary>Called before a release or a stop takes effect, so a control plane hears about it
    /// while the link that carries the news is still up. Returns true if the plane acknowledged.
    ///
    /// The runner does not care about the answer beyond logging it: an owner reclaiming their own
    /// hardware is never gated on a remote service being reachable.</summary>
    public Func<ControlEvent, CancellationToken, Task<bool>>? ControlEventSink { get; set; }

    public event Action<LogLine>? LineWritten;

    /// <summary>Load every instance from disk and re-attach to any that are still running.
    ///
    /// Adoption is the normal path, not a recovery one: restarting the runner to pick up a new version
    /// must not take a server full of players down with it.</summary>
    public void LoadAndAdopt()
    {
        foreach (var spec in _store.List())
        {
            var instance = new SupervisedInstance(_store, _builds, spec);
            instance.LineWritten += line => LineWritten?.Invoke(line);
            _instances[spec.Name] = instance;
            instance.TryAdopt();
        }
    }

    public IReadOnlyList<SupervisedInstance> All() =>
        _instances.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public SupervisedInstance? Find(string name) =>
        _instances.TryGetValue(name, out var instance) ? instance : null;

    public SupervisedInstance Require(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"no instance '{name}'");

    // ---- mutation, gated on control mode ----

    public SupervisedInstance Create(InstanceSpec spec)
    {
        InstanceStore.ValidateName(spec.Name);
        if (_store.Exists(spec.Name))
            throw new InvalidOperationException($"instance '{spec.Name}' already exists");

        _store.Save(spec);
        _store.EnsureDefaultConfig(spec);

        var instance = new SupervisedInstance(_store, _builds, spec);
        instance.LineWritten += line => LineWritten?.Invoke(line);
        _instances[spec.Name] = instance;
        return instance;
    }

    public void UpdateSpec(InstanceSpec spec, ControlOrigin origin)
    {
        var instance = Require(spec.Name);
        Authorize(instance.Spec, origin);

        // Control fields are runner state, not operator input. Accepting them from a spec edit would
        // let a local PATCH quietly hand the box to an orchestrator, or take it back without the
        // release path that raises the alert.
        var merged = spec with
        {
            ControlMode = instance.Spec.ControlMode,
            ControllerUrl = instance.Spec.ControllerUrl,
            GrantedScopes = instance.Spec.GrantedScopes,
            ControlledSince = instance.Spec.ControlledSince,
        };

        _store.Save(merged);
        instance.UpdateSpec(merged);
    }

    public async Task StartAsync(string name, ControlOrigin origin, CancellationToken ct = default)
    {
        var instance = Require(name);
        Authorize(instance.Spec, origin);
        await instance.StartAsync(ct);
    }

    public async Task RestartAsync(string name, ControlOrigin origin, CancellationToken ct = default)
    {
        var instance = Require(name);
        Authorize(instance.Spec, origin);
        await instance.StopAsync(ct: ct);
        await instance.StartAsync(ct);
    }

    public async Task DrainAsync(string name, DrainRequest request, ControlOrigin origin,
        CancellationToken ct = default)
    {
        var instance = Require(name);
        Authorize(instance.Spec, origin);
        await instance.DrainAsync(request.Message, TimeSpan.FromSeconds(request.TimeoutSeconds), ct);
    }

    public async Task SendAsync(string name, string command, ControlOrigin origin,
        CancellationToken ct = default)
    {
        var instance = Require(name);
        Authorize(instance.Spec, origin);
        await instance.SendAsync(command, ct);
    }

    public void Delete(string name, ControlOrigin origin)
    {
        var instance = Require(name);
        Authorize(instance.Spec, origin);
        if (instance.State is not InstanceState.Stopped)
            throw new InvalidOperationException($"stop '{name}' before deleting it");

        instance.Dispose();
        _instances.TryRemove(name, out _);
        _store.Delete(name);
    }

    /// <summary>Stop. Always available to the host owner, whatever the control mode, because it is
    /// their hardware. Raises a control event first when the instance is orchestrated.</summary>
    public async Task StopAsync(string name, ControlOrigin origin, string initiator,
        string? reason = null, CancellationToken ct = default)
    {
        var instance = Require(name);

        if (instance.Spec.ControlMode == ControlMode.Orchestrated && origin == ControlOrigin.Local)
            await RaiseAsync(instance, ControlEventKind.Stopped, null, initiator, reason, ct);

        await instance.StopAsync(ct: ct);
        Audit(instance, origin, initiator, "stop", reason);
    }

    /// <summary>Return an orchestrated instance to local control without stopping it.
    ///
    /// end-of-match is the default because most releases are an operator wanting their box back rather
    /// than an emergency, and a one-click graceful option is what keeps the critical-alert queue worth
    /// reading. `now` is always available and gated on nothing.</summary>
    public async Task ReleaseAsync(string name, ReleaseRequest request, string initiator,
        CancellationToken ct = default)
    {
        var instance = Require(name);
        if (instance.Spec.ControlMode == ControlMode.Local)
            return;

        await RaiseAsync(instance, ControlEventKind.Released, request.When, initiator,
            request.Reason, ct);

        if (request.When == ReleaseWhen.EndOfMatch && instance.MatchLive)
        {
            _ = WaitForMatchEndThenReleaseAsync(instance, ct);
            return;
        }

        ApplyRelease(instance);
        Audit(instance, ControlOrigin.Local, initiator, "release", request.Reason);
    }

    /// <summary>Hand an instance to an orchestrator after the runner has completed the key handshake.
    /// Never reachable from a spec edit: see the comment in <see cref="UpdateSpec"/>.</summary>
    public void Adopt(string name, string controllerUrl, IReadOnlyList<string> scopes)
    {
        var instance = Require(name);
        var spec = instance.Spec with
        {
            ControlMode = ControlMode.Orchestrated,
            ControllerUrl = controllerUrl,
            GrantedScopes = scopes,
            ControlledSince = DateTimeOffset.UtcNow,
        };
        _store.Save(spec);
        instance.UpdateSpec(spec);
    }

    private async Task WaitForMatchEndThenReleaseAsync(SupervisedInstance instance, CancellationToken ct)
    {
        try
        {
            // Bounded: a server whose match never ends must not hold the release forever. An hour is
            // longer than any real match and short enough that a forgotten release still lands.
            var deadline = DateTimeOffset.UtcNow.AddHours(1);
            while (instance.MatchLive && DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        catch (OperationCanceledException) { }

        ApplyRelease(instance);
    }

    private void ApplyRelease(SupervisedInstance instance)
    {
        var spec = instance.Spec with
        {
            ControlMode = ControlMode.Local,
            ControllerUrl = null,
            GrantedScopes = null,
            ControlledSince = null,
        };
        _store.Save(spec);
        instance.UpdateSpec(spec);
    }

    /// <summary>Send the control event, wait briefly for an ack, proceed either way.</summary>
    private async Task RaiseAsync(SupervisedInstance instance, ControlEventKind kind, ReleaseWhen? when,
        string initiator, string? reason, CancellationToken ct)
    {
        var sink = ControlEventSink;
        if (sink is null)
            return;

        var status = instance.Status();
        var evt = new ControlEvent
        {
            EventId = Guid.NewGuid().ToString("n"),
            RunnerId = RunnerIdentity.Current,
            InstanceName = instance.Name,
            Kind = kind,
            When = when,
            PlayersConnected = status.Players ?? 0,
            MatchLive = status.MatchLive,
            MatchElapsedSeconds = status.MatchElapsedSeconds,
            Map = status.Map,
            Gametype = status.Gametype,
            Initiator = initiator,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = reason,
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ManagementProtocol.ControlEventAckTimeoutMs);
            await sink(evt, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the plane is unreachable. Not an error, and never a reason to block.
        }
        catch (Exception)
        {
            // Same. The owner's exit cannot depend on anything remote succeeding.
        }
    }

    private static void Authorize(InstanceSpec spec, ControlOrigin origin)
    {
        if (spec.ControlMode == ControlMode.Orchestrated && origin == ControlOrigin.Local)
            throw new InstanceOrchestratedException(spec);
        if (spec.ControlMode == ControlMode.Local && origin == ControlOrigin.Orchestrator)
            throw new InvalidOperationException(
                $"instance '{spec.Name}' is under local control; the orchestrator cannot operate it");
    }

    private void Audit(SupervisedInstance instance, ControlOrigin origin, string actor,
        string action, string? reason)
    {
        var line = ManagementProtocol.Serialize(new
        {
            at = DateTimeOffset.UtcNow,
            origin = origin.ToString().ToLowerInvariant(),
            actor,
            action,
            reason,
        });
        try
        {
            var path = _store.PathsFor(instance.Name).AuditPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException) { }
    }

    // ---- health ----

    /// <summary>Probe every running instance on a timer. Liveness is process-alive AND answering
    /// getinfo; a wedged server that still holds its process is exactly what this is for.</summary>
    public void StartHealthLoop(TimeSpan interval, CancellationToken ct = default)
    {
        _health = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _health.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var instance in All().Where(i => i.State == InstanceState.Running))
                {
                    try
                    {
                        await instance.ProbeAsync(token);
                        await MaybeRestartOnScheduleAsync(instance, token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { /* a failed probe is data, not an error */ }
                }

                try { await Task.Delay(interval, token); }
                catch (OperationCanceledException) { return; }
            }
        }, token);
    }

    /// <summary>Unattended restart, if this instance has a window and we are in it.
    ///
    /// Skipped while an orchestrator holds the instance: a scheduled restart is a local policy, and
    /// firing it against a server somebody else is operating is the concurrent-writer problem the
    /// control mode exists to prevent.</summary>
    private async Task MaybeRestartOnScheduleAsync(SupervisedInstance instance, CancellationToken ct)
    {
        if (instance.Spec.RestartAt is not { Length: > 0 } window)
            return;
        if (instance.Spec.ControlMode == ControlMode.Orchestrated)
            return;
        if (!TryParseWindow(window, out var hour, out var minute))
            return;

        var now = DateTimeOffset.Now;
        if (now.Hour != hour || now.Minute != minute)
            return;

        // The window stays current for a whole minute and the health loop runs every fifteen seconds,
        // so without this it would fire four times.
        if (instance.LastScheduledRestart is { } last && (now - last).TotalMinutes < 2)
            return;

        if (instance.Spec.RestartOnlyWhenEmpty)
        {
            var status = instance.Status();
            if (status.Players is > 0)
                return; // take the next window rather than kick a live server
        }

        instance.LastScheduledRestart = now;
        await instance.StopAsync(ct: ct);
        await instance.StartAsync(ct);
    }

    /// <summary>"HH:mm" in the box's local time.</summary>
    public static bool TryParseWindow(string value, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = value.Split(':');
        return parts.Length == 2
               && int.TryParse(parts[0], out hour) && hour is >= 0 and <= 23
               && int.TryParse(parts[1], out minute) && minute is >= 0 and <= 59;
    }

    public void Dispose()
    {
        _health?.Cancel();
        _health?.Dispose();
        foreach (var instance in _instances.Values)
            instance.Dispose();
    }
}

/// <summary>A stable id for this box, generated once and kept beside the runner's other state. It is
/// what a control plane correlates instances and control events against, so it must survive a runner
/// restart and a version upgrade.</summary>
public static class RunnerIdentity
{
    private static string? _cached;

    public static string Current => _cached ??= "unset";

    public static string LoadOrCreate(LauncherPaths paths)
    {
        var file = Path.Combine(paths.RunnerDir, "runner-id");
        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (existing.Length > 0)
                return _cached = existing;
        }

        var id = Guid.NewGuid().ToString("n");
        Directory.CreateDirectory(paths.RunnerDir);
        File.WriteAllText(file, id);
        return _cached = id;
    }
}
