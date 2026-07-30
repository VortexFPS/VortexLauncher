namespace Launcher.Protocol;

public enum ControlEventKind
{
    /// <summary>The host owner returned the instance to local control. It keeps running.</summary>
    Released,

    /// <summary>The host owner shut the instance down.</summary>
    Stopped,
}

public enum ReleaseWhen
{
    /// <summary>At the end of the current match, or immediately if no match is live. The default,
    /// because most releases are an operator wanting their box back rather than an emergency, and a
    /// one-click graceful option is what keeps the critical-alert queue meaningful.</summary>
    EndOfMatch,

    /// <summary>Now. Always available, gated on nothing.</summary>
    Now,
}

/// <summary>What a runner sends to a control plane when the host owner uses one of their two exits.
///
/// Captured at the moment of action, not reconstructed afterward, because the connection that would
/// carry it is about to be severed by the very action it describes. The runner sends this first, waits
/// <see cref="ManagementProtocol.ControlEventAckTimeoutMs"/> for an ack, then proceeds regardless.</summary>
public sealed record ControlEvent
{
    public required string EventId { get; init; }
    public required string RunnerId { get; init; }
    public required string InstanceName { get; init; }
    public required ControlEventKind Kind { get; init; }
    public ReleaseWhen? When { get; init; }

    /// <summary>Humans only. This plus <see cref="MatchLive"/> is what separates a critical alert from
    /// a routine one.</summary>
    public int PlayersConnected { get; init; }
    public bool MatchLive { get; init; }
    public int? MatchElapsedSeconds { get; init; }

    public string? Map { get; init; }
    public string? Gametype { get; init; }

    /// <summary>Local OS user, or the WebServer session identity that asked.</summary>
    public required string Initiator { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Free text an operator may attach. Worth reading before treating a mid-match release as
    /// a problem.</summary>
    public string? Reason { get; init; }
}

/// <summary>Severity a control plane assigns on ingest. Not sent by the runner: the runner reports
/// facts, and the plane decides what they mean.</summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

public static class ControlEventSeverity
{
    /// <summary>Players connected to a live match is the case this whole mechanism exists for.
    /// Everything else is a warning: an operator taking back an idle box is their right and not an
    /// incident.</summary>
    public static AlertSeverity For(ControlEvent e) =>
        e is { PlayersConnected: > 0, MatchLive: true } ? AlertSeverity.Critical : AlertSeverity.Warning;
}

/// <summary>Raised by a control plane when a runner link drops with no preceding
/// <see cref="ControlEvent"/>.
///
/// Kept distinct from a release on purpose. An acked event is a clean, explained exit; a socket that
/// simply closed may be a network blip that resolves on reconnect. Rendering the second as the first
/// would fill the alert queue with noise and bury the mid-match case it exists to surface.</summary>
public sealed record LostContactEvent
{
    public required string RunnerId { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public required IReadOnlyList<string> OrchestratedInstances { get; init; }

    /// <summary>Set when the runner comes back, which is the common outcome.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}
