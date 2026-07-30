namespace Launcher.Protocol;

/// <summary>Which control plane operates an instance. Exactly one at a time.
///
/// This is a mode, not a merged permission set, and that is the whole design. Two planes pointed at
/// one instance never race, because the runner routes mutating commands by mode and rejects the other
/// plane outright. Ownership of the box stops meaning "the right to operate" and starts meaning "the
/// right to exit", which is the correct meaning: it is the operator's hardware.</summary>
public enum ControlMode
{
    /// <summary>The host operator's own WebServer operates it.</summary>
    Local,

    /// <summary>Conductor operates it. The owner keeps read access and exactly two actions: release
    /// and stop.</summary>
    Orchestrated,
}

public enum RestartPolicy
{
    Always,
    OnFailure,
    Never,
}

public enum InstanceState
{
    Stopped,
    Starting,
    Running,
    Draining,
    Stopping,
    /// <summary>Crashed repeatedly inside the flap window and was stopped rather than bounced.</summary>
    Flapping,
    Failed,
}

/// <summary>Everything an operator configures about an instance. Persisted by the runner as
/// instance.json and the body of instance create and patch.</summary>
public sealed record InstanceSpec
{
    public required string Name { get; init; }
    public required string Map { get; init; }
    public string Gametype { get; init; } = "dm";

    /// <summary>Always explicit. A server that started is not a server that bound, and the runner
    /// reads the real bind line out of stdout before reporting it running.</summary>
    public required int Port { get; init; }

    public int MaxPlayers { get; init; } = 16;
    public string? Hostname { get; init; }

    /// <summary>Build id this instance runs. Null means "whatever the store considers current",
    /// resolved at start and then recorded, so a restart cannot silently change versions.</summary>
    public string? BuildId { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnFailure;

    /// <summary>Extra command-line arguments, appended after the runner's own.</summary>
    public IReadOnlyList<string>? ExtraArgs { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>sha256 of every content package this instance should have. The runner fetches what it
    /// is missing and verifies before installing; nothing is ever pushed at it.</summary>
    public IReadOnlyList<string>? ContentSet { get; init; }

    /// <summary>Unattended restart window, as "HH:mm" in the box's local time, or null for none.
    ///
    /// Local time rather than UTC on purpose: an operator picking 05:00 means the quiet hour where
    /// their players are, and making them convert it is how a restart lands in the middle of European
    /// prime time.</summary>
    public string? RestartAt { get; init; }

    /// <summary>Skip a scheduled restart while players are connected, and take the next window
    /// instead. On by default: a nightly restart that kicks a live match is worse than a server that
    /// stays up an extra day.</summary>
    public bool RestartOnlyWhenEmpty { get; init; } = true;

    public ControlMode ControlMode { get; init; } = ControlMode.Local;

    /// <summary>Which Conductor holds control, when orchestrated. Shown in the owner's banner.</summary>
    public string? ControllerUrl { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
    public DateTimeOffset? ControlledSince { get; init; }
}

/// <summary>Live view of one instance: supervisor state plus the most recent getinfo snapshot.</summary>
public sealed record InstanceStatus
{
    public required string Name { get; init; }
    public required InstanceState State { get; init; }
    public required ControlMode ControlMode { get; init; }

    public string? BuildId { get; init; }
    public int? Pid { get; init; }
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>From the live getinfo probe, which is also the liveness check. Null when the server
    /// has not answered yet, which is different from answering with zero players.</summary>
    public string? Map { get; init; }
    public string? Gametype { get; init; }
    public int? Players { get; init; }
    public int? Bots { get; init; }
    public int? MaxPlayers { get; init; }

    public bool MatchLive { get; init; }
    public int? MatchElapsedSeconds { get; init; }

    public double? CpuPercent { get; init; }
    public long? MemoryBytes { get; init; }

    public int RestartCount { get; init; }
    public string? LastExitReason { get; init; }
}

/// <summary>Runner-level view, for the plane's dashboard.</summary>
public sealed record RunnerStatus
{
    public required string RunnerId { get; init; }
    public required string Version { get; init; }
    public required string Platform { get; init; }
    public required string Hostname { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    public long? DiskFreeBytes { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public long? MemoryUsedBytes { get; init; }

    public required IReadOnlyList<InstanceStatus> Instances { get; init; }

    /// <summary>Set when this runner is linked to a Conductor, whatever any single instance's mode
    /// is. A linked runner with every instance local is a normal state.</summary>
    public string? ConductorUrl { get; init; }
}
