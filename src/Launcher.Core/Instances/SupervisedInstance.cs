using System.Diagnostics;
using System.Net;
using System.Text;
using Launcher.Core.GameControl;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>One supervised game-server process.
///
/// Owns the child's stdin, which is the primary command channel and has no network surface at all.
/// rcon is the fallback for an adopted orphan whose stdin died with the previous runner, and getinfo
/// is the health check. That preference order is deliberate: sending a command over loopback UDP that
/// could have gone down a pipe adds an authentication surface for nothing.</summary>
public sealed class SupervisedInstance : IDisposable
{
    /// <summary>How much recent output this instance keeps in memory. Public because it is also the
    /// ceiling the log route clamps `?tail=` to: asking for more than the ring holds is not an error,
    /// but there is no honest answer above this number and the request should not size an allocation
    /// off a URL.</summary>
    public const int LogRingLines = 2000;

    private readonly InstanceStore _store;
    private readonly BuildStore _builds;
    private readonly Queue<LogLine> _ring = new();
    private readonly object _ringGate = new();
    private readonly EventLogParser _events = new();
    private readonly GameQueryClient _query = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamWriter? _logFile;
    private CancellationTokenSource? _lifetime;
    private ServerInfo? _lastInfo;
    private DateTimeOffset? _startedAt;
    private readonly List<DateTimeOffset> _recentStarts = [];

    // CPU is a rate, not a reading: it only exists as a difference between two samples. Holding the
    // previous pair is what turns Process.TotalProcessorTime into a percentage.
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastCpuSample;
    private double? _cpuPercent;

    /// <summary>The last scheduled-restart window this instance acted on, so a window that stays
    /// current for a whole minute does not fire sixty times.</summary>
    internal DateTimeOffset? LastScheduledRestart { get; set; }

    public SupervisedInstance(InstanceStore store, BuildStore builds, InstanceSpec spec)
    {
        _store = store;
        _builds = builds;
        Spec = spec;
    }

    public InstanceSpec Spec { get; private set; }
    public string Name => Spec.Name;
    public InstanceState State { get; private set; } = InstanceState.Stopped;
    public int RestartCount { get; private set; }
    public string? LastExitReason { get; private set; }

    /// <summary>Raised for every captured line. The runner link forwards these to subscribers, after
    /// filtering chat for planes without the chat-read scope.</summary>
    public event Action<LogLine>? LineWritten;

    /// <summary>Flap window: this many starts inside <see cref="FlapWindow"/> stops the instance rather
    /// than continuing to bounce it. A server that crashes on load will otherwise restart forever and
    /// the only symptom is a log nobody is reading.</summary>
    public int FlapThreshold { get; init; } = 5;
    public TimeSpan FlapWindow { get; init; } = TimeSpan.FromMinutes(5);

    public bool MatchLive => _events.MatchLive;
    public int? MatchElapsedSeconds => _events.MatchElapsedSeconds;

    public InstanceStatus Status() => new()
    {
        Name = Name,
        State = State,
        ControlMode = Spec.ControlMode,
        BuildId = Spec.BuildId,
        Pid = _process is { HasExited: false } ? _process.Id : null,
        StartedAt = _startedAt,
        Map = _lastInfo?.Map ?? _events.Map,
        Gametype = _lastInfo?.Gametype ?? _events.Gametype,
        Players = _lastInfo?.Players,
        Bots = _lastInfo?.Bots,
        MaxPlayers = _lastInfo?.MaxPlayers ?? Spec.MaxPlayers,
        MatchLive = _events.MatchLive,
        MatchElapsedSeconds = _events.MatchElapsedSeconds,
        CpuPercent = _cpuPercent,
        MemoryBytes = SafeWorkingSet(),
        RestartCount = RestartCount,
        LastExitReason = LastExitReason,
    };

    public void UpdateSpec(InstanceSpec spec) => Spec = spec;

    // ---- lifecycle ----

    /// <summary>Start the process. Async in signature only: spawning is synchronous, and the pumps it
    /// kicks off are deliberately not awaited here because they run for the life of the server. Kept
    /// async so callers do not have to change if starting ever needs to wait for something.</summary>
#pragma warning disable CS1998
    public async Task StartAsync(CancellationToken ct = default)
#pragma warning restore CS1998
    {
        if (State is InstanceState.Running or InstanceState.Starting)
            return;

        var build = ResolveBuild()
            ?? throw new InvalidOperationException(
                $"instance '{Name}' has no usable build; install one or set build_id");

        var gameDir = _builds.GameDirOf(build);
        var exe = PlatformKey.ExecutableCandidates(build.PlatformKey)
            .Select(rel => Path.Combine(gameDir, rel))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"no game binary in {gameDir}; the build may be incomplete");

        var instance = _store.PathsFor(Name);
        instance.EnsureCreated();
        _store.EnsureDefaultConfig(Spec);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("--dedicated");
        // Always explicit. Letting the game pick means the runner does not know what to health-check,
        // and the 26000 default is the port most likely to already be occupied.
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(Spec.Port.ToString());
        psi.ArgumentList.Add("--userdir");
        psi.ArgumentList.Add(instance.DataDir);
        if (!string.IsNullOrEmpty(Spec.Map))
        {
            psi.ArgumentList.Add("+map");
            psi.ArgumentList.Add(Spec.Map);
        }
        AddAnnounceArgs(psi);
        foreach (var arg in Spec.ExtraArgs ?? [])
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in Spec.Environment ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        State = InstanceState.Starting;
        _events.Reset();
        _lastInfo = null;

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{Name}'");
        _startedAt = DateTimeOffset.UtcNow;
        _stdin = _process.StandardInput;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logFile = new StreamWriter(instance.LogPath(_startedAt.Value), append: true) { AutoFlush = true };
        File.WriteAllText(instance.PidPath, _process.Id.ToString());

        _recentStarts.Add(_startedAt.Value);
        _recentStarts.RemoveAll(t => DateTimeOffset.UtcNow - t > FlapWindow);

        _ = PumpAsync(_process.StandardOutput, LogStream.Stdout, _lifetime.Token);
        _ = PumpAsync(_process.StandardError, LogStream.Stderr, _lifetime.Token);
        _ = WatchExitAsync(_process, _lifetime.Token);

        Emit(LogStream.Runner, $"started pid {_process.Id} on port {Spec.Port} from build {build.Id}");
        State = InstanceState.Running;
    }

    /// <summary>Pin the master-announce cvars on the launch line, so a runner-started server is
    /// actually visible in the server browser. Without them sv_public stays unregistered at 0 and the
    /// server deliberately announces nothing, which makes every instance on the box invisible.
    ///
    /// The command line rather than server.cfg, because these are runner state and runner state
    /// changes underneath a server that is already installed. `vortex runner rotate-key` replaces the
    /// control key; link and unlink flip the offer. server.cfg is written once by EnsureDefaultConfig
    /// and never rewritten - it belongs to the operator - so a fingerprint baked into it would keep
    /// announcing a key this box no longer holds, offering control that can never be proved. Read from
    /// the runner config at every start instead, which is what makes rotate-key's promise true:
    /// running servers announce the old fingerprint only until they restart. It also means the runner
    /// never edits a file the operator owns.
    ///
    /// The game applies --cvar pins after server.cfg (DS-5), so these beat a stale copy somebody left
    /// in there, and an instance's own ExtraArgs are appended after these and still win.</summary>
    private void AddAnnounceArgs(ProcessStartInfo psi)
    {
        // From disk on every start, not captured at construction: `vortex runner rotate-key` runs in
        // its own process, and a supervisor holding a snapshot would launch with the superseded key.
        var runner = new RunnerConfigStore(_store.LauncherPaths).Load();

        // Explicit in both directions. The 1 is what makes a listed instance announce at all; the 0
        // is what makes Spec.Listed authoritative over a `set sv_public 1` in an operator's server.cfg,
        // so turning listing off for one instance actually turns it off.
        Pin(psi, "sv_public", Spec.Listed ? "1" : "0");

        // Only when this box was pointed somewhere else. Unset means the game's own default, which is
        // the address the announce protocol names.
        if (runner.MasterUrl is { Length: > 0 } master)
            Pin(psi, "sv_master_url", master);

        // The adoption offer and the key it binds to, together or not at all. An offer with no
        // fingerprint fails validation at the master and costs the whole listing, turning a runner
        // that is merely unlinked into a server nobody can find. Cleared rather than omitted when this
        // box is not offering, so an earlier link's fingerprint cannot survive in server.cfg.
        var offering = runner.ConductorControl && runner.ControlKeyFingerprint is { Length: > 0 };
        Pin(psi, "conductor_control", offering ? "1" : "0");
        Pin(psi, "conductor_control_key", offering ? runner.ControlKeyFingerprint! : "");
    }

    private static void Pin(ProcessStartInfo psi, string cvar, string value)
    {
        psi.ArgumentList.Add("--cvar");
        psi.ArgumentList.Add(cvar);
        psi.ArgumentList.Add(value);
    }

    /// <summary>Ask the server to quit over stdin, then wait, then kill.
    ///
    /// The polite path matters: a killed server does not write its ban list or close its eventlog, and
    /// both losses look like data corruption to whoever finds them later.</summary>
    public async Task StopAsync(TimeSpan? grace = null, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
        {
            State = InstanceState.Stopped;
            return;
        }

        State = InstanceState.Stopping;
        var deadline = grace ?? TimeSpan.FromSeconds(20);

        try
        {
            await SendAsync("quit", ct);
        }
        catch (IOException)
        {
            // stdin already gone; fall through to the wait and then the kill
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Emit(LogStream.Runner, $"did not exit within {deadline.TotalSeconds:F0}s; killing");
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        Cleanup();
        State = InstanceState.Stopped;
    }

    /// <summary>Warn, wait for the server to empty, then stop. Falls through to a plain stop on
    /// timeout, because a server nobody leaves still has to be updatable.</summary>
    public async Task DrainAsync(string? message, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return;

        State = InstanceState.Draining;
        if (!string.IsNullOrWhiteSpace(message))
            await SendAsync($"say {message}", ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var info = await ProbeAsync(ct);
            if (info is null || info.Players == 0)
                break;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        await StopAsync(ct: ct);
    }

    /// <summary>Write a console command to the child's stdin.</summary>
    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        if (_stdin is null)
            throw new IOException($"instance '{Name}' has no stdin; it is not running under this runner");
        await _stdin.WriteLineAsync(command.AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        Emit(LogStream.Runner, $"> {command}");
    }

    /// <summary>Fallback command path for an instance this runner did not start. Requires
    /// rcon_password in the instance's server.cfg, which is why stdin is preferred whenever it
    /// exists.</summary>
    public async Task<string> SendViaRconAsync(string command, string password,
        CancellationToken ct = default)
    {
        var client = new RconClient(new IPEndPoint(IPAddress.Loopback, Spec.Port), password);
        return await client.ExecuteAsync(command, ct);
    }

    public async Task<ServerInfo?> ProbeAsync(CancellationToken ct = default)
    {
        SampleCpu();
        _lastInfo = await _query.GetInfoAsync(new IPEndPoint(IPAddress.Loopback, Spec.Port), ct);
        return _lastInfo;
    }

    /// <summary>Process CPU as a share of one core, sampled against the previous reading.
    ///
    /// Divided by processor count so the number means the same thing on a 4-core VPS and a 64-core
    /// host: 100% is the whole machine, not one core. An operator comparing two boxes should not have
    /// to know how many cores each has to read the dashboard.</summary>
    private void SampleCpu()
    {
        try
        {
            if (_process is null || _process.HasExited)
            {
                _cpuPercent = null;
                return;
            }

            _process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var cpu = _process.TotalProcessorTime;

            if (_lastCpuSample != default)
            {
                var wall = (now - _lastCpuSample).TotalMilliseconds;
                // Two samples in the same millisecond produce a meaningless ratio, so wait.
                if (wall >= 1)
                {
                    var used = (cpu - _lastCpuTime).TotalMilliseconds;
                    _cpuPercent = Math.Round(
                        Math.Clamp(used / (wall * Environment.ProcessorCount) * 100, 0, 100), 1);
                }
            }

            _lastCpuTime = cpu;
            _lastCpuSample = now;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or NotSupportedException)
        {
            // An adopted process on a locked-down box may refuse to report times. A missing metric is
            // not a reason to fail a health check.
            _cpuPercent = null;
        }
    }

    /// <summary>Alive and answering. A process that is up but not responding to getinfo is not a
    /// running server, and calling it one is how a wedged instance stays listed as healthy.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return false;
        return await ProbeAsync(ct) is not null;
    }

    public bool IsFlapping =>
        _recentStarts.Count(t => DateTimeOffset.UtcNow - t <= FlapWindow) >= FlapThreshold;

    /// <summary>Adopt a process this runner did not start, from the pidfile left by the one that did.
    ///
    /// stdin is gone: it belonged to the dead parent. That is the whole reason the rcon path exists.
    /// A runner restart must not take the servers with it, so re-attaching to a live child is the
    /// normal case rather than a recovery path.</summary>
    public bool TryAdopt()
    {
        var pidPath = _store.PathsFor(Name).PidPath;
        if (!File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid))
            return false;

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            _process = process;
            _startedAt = process.StartTime.ToUniversalTime();
            _stdin = null; // not ours; commands go over rcon
            State = InstanceState.Running;
            Emit(LogStream.Runner, $"adopted running pid {pid}; stdin unavailable, using rcon");
            return true;
        }
        catch (ArgumentException)
        {
            return false; // no such process: a stale pidfile from an unclean shutdown
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public IReadOnlyList<LogLine> Tail(int lines)
    {
        lock (_ringGate)
            return _ring.Reverse().Take(lines).Reverse().ToList();
    }

    // ---- internals ----

    private BuildRecord? ResolveBuild()
    {
        if (Spec.BuildId is not null)
            return _builds.Get(Spec.BuildId);
        return _builds.List().FirstOrDefault();
    }

    private async Task PumpAsync(StreamReader reader, LogStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;

                var evt = _events.Feed(line);
                Emit(evt is null ? stream : LogStream.Event, line, evt);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task WatchExitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (State is InstanceState.Stopping or InstanceState.Draining)
            return;

        LastExitReason = $"exit code {process.ExitCode}";
        Emit(LogStream.Runner, $"exited with code {process.ExitCode}");
        _events.Reset();

        if (IsFlapping)
        {
            State = InstanceState.Flapping;
            Emit(LogStream.Runner,
                $"{FlapThreshold} starts within {FlapWindow.TotalMinutes:F0} minutes; not restarting");
            return;
        }

        var shouldRestart = Spec.RestartPolicy switch
        {
            RestartPolicy.Always => true,
            // DS-4 gives boot failures their own exit codes. Restarting a server that cannot parse its
            // own config just repeats the failure at a slower rate.
            RestartPolicy.OnFailure => process.ExitCode != 0,
            _ => false,
        };

        if (!shouldRestart)
        {
            State = InstanceState.Stopped;
            return;
        }

        RestartCount++;
        var backoff = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, RestartCount))));
        Emit(LogStream.Runner, $"restarting in {backoff.TotalSeconds:F0}s (attempt {RestartCount})");

        try
        {
            await Task.Delay(backoff, ct);
            await StartAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            State = InstanceState.Failed;
            LastExitReason = ex.Message;
            Emit(LogStream.Runner, $"restart failed: {ex.Message}");
        }
    }

    private void Emit(LogStream stream, string text, GameEvent? evt = null)
    {
        var line = new LogLine
        {
            InstanceName = Name,
            Stream = stream,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = evt?.Type,
            IsChat = evt?.IsChat ?? false,
        };

        lock (_ringGate)
        {
            _ring.Enqueue(line);
            while (_ring.Count > LogRingLines)
                _ring.Dequeue();
        }

        try { _logFile?.WriteLine($"{line.Timestamp:O} [{stream}] {text}"); }
        catch (IOException) { }

        LineWritten?.Invoke(line);
    }

    private long? SafeWorkingSet()
    {
        try
        {
            if (_process is null || _process.HasExited)
                return null;
            _process.Refresh();
            return _process.WorkingSet64;
        }
        catch (InvalidOperationException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private void Cleanup()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        _stdin = null;

        try { _logFile?.Dispose(); } catch (IOException) { }
        _logFile = null;

        var pidPath = _store.PathsFor(Name).PidPath;
        try { if (File.Exists(pidPath)) File.Delete(pidPath); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        Cleanup();
        _process?.Dispose();
    }
}
