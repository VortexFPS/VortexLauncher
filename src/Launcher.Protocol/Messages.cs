namespace Launcher.Protocol;

/// <summary>A control-plane request, tunneled to a runner over the link.
///
/// The envelope is the runner's own REST semantics rather than a second command vocabulary. That is
/// what keeps a control plane a proxy with auth instead of a parallel management implementation:
/// anything the runner's API learns, every plane gets without a protocol change.</summary>
public sealed record CommandEnvelope
{
    /// <summary>Idempotency key. A command that was queued while the runner was offline may arrive
    /// twice after a reconnect, and replaying a restart is not harmless.</summary>
    public required string CommandId { get; init; }

    public required string Method { get; init; }

    /// <summary>Path under <see cref="ManagementProtocol.ApiPrefix"/>, for example
    /// /api/v1/instances/eu-1/start.</summary>
    public required string Path { get; init; }

    public string? Body { get; init; }

    /// <summary>Queued commands expire. A restart that was ordered an hour ago, while the box was
    /// unreachable, is usually not something anyone still wants.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Who asked, for the runner's own audit log. The runner records this independently of
    /// the plane, which is what lets the host owner see what was done to their box.</summary>
    public required string ActorId { get; init; }

    /// <summary>Scopes the plane claims for this command. Advisory: the runner enforces against the
    /// grant it stored at link time, never against what the request asserts.</summary>
    public IReadOnlyList<string>? ClaimedScopes { get; init; }
}

public sealed record CommandResult
{
    public required string CommandId { get; init; }

    /// <summary>HTTP status the same call would have returned locally.</summary>
    public required int Status { get; init; }

    public string? Body { get; init; }
}

public enum RunnerFrameKind
{
    /// <summary>First frame after connect: identity, version, scopes the runner will honour.</summary>
    Hello,
    Heartbeat,
    Status,
    LogLine,
    ControlEvent,
    CommandResult,
}

public enum PlaneFrameKind
{
    HelloAck,
    Command,
    /// <summary>Acknowledges a control event. Best-effort: the runner proceeds without it.</summary>
    ControlEventAck,
    /// <summary>Ask the runner to stream an instance's log until told otherwise.</summary>
    Subscribe,
    Unsubscribe,
}

/// <summary>Runner to plane. One envelope type with a discriminator, so a frame can be logged and
/// replayed without knowing which payload it carries.</summary>
public sealed record RunnerFrame
{
    public required RunnerFrameKind Kind { get; init; }
    public required string RunnerId { get; init; }
    public int ProtocolVersion { get; init; } = ManagementProtocol.Version;

    public RunnerHello? Hello { get; init; }
    public RunnerStatus? Status { get; init; }
    public LogLine? Log { get; init; }
    public ControlEvent? ControlEvent { get; init; }
    public CommandResult? Result { get; init; }
}

public sealed record PlaneFrame
{
    public required PlaneFrameKind Kind { get; init; }
    public int ProtocolVersion { get; init; } = ManagementProtocol.Version;

    public CommandEnvelope? Command { get; init; }
    public string? EventId { get; init; }
    public string? InstanceName { get; init; }
    public PlaneHelloAck? HelloAck { get; init; }
}

public sealed record RunnerHello
{
    public required string RunnerId { get; init; }
    public required string Version { get; init; }
    public required string Platform { get; init; }
    public required string Hostname { get; init; }

    /// <summary>sha256 of this runner's public key, lowercase hex. The same value its game servers
    /// put in their announce, which is what ties an adoption offer to the box that made it.</summary>
    public string? ControlKeyFingerprint { get; init; }

    /// <summary>Signature over the challenge the plane issued, proving possession of the private half.
    /// Acceptance in a panel grants nothing without this.</summary>
    public string? ChallengeSignature { get; init; }

    public required IReadOnlyList<string> Instances { get; init; }
}

public sealed record PlaneHelloAck
{
    public required bool Accepted { get; init; }

    /// <summary>What this plane is actually allowed to do, as stored at link time. The runner enforces
    /// this and ignores anything a later command claims for itself.</summary>
    public IReadOnlyList<string>? GrantedScopes { get; init; }

    public string? Detail { get; init; }
}

public enum LogStream
{
    Stdout,
    Stderr,
    /// <summary>Parsed eventlog line: joins, kills, votes, chat.</summary>
    Event,
    /// <summary>The runner's own supervision messages, not the game's.</summary>
    Runner,
}

public sealed record LogLine
{
    public required string InstanceName { get; init; }
    public required LogStream Stream { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Set for parsed eventlog lines: chat, chat_team, join, kill and so on.</summary>
    public string? EventType { get; init; }

    /// <summary>True for the chat variants. A plane without chat-read must filter on this before it
    /// forwards a log stream to a viewer.</summary>
    public bool IsChat { get; init; }
}

/// <summary>Error body for every non-2xx on the runner API.</summary>
public sealed record ApiError
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>Set on <see cref="ApiErrorCodes.InstanceOrchestrated"/>: which Conductor holds control
    /// and how to get out. Sending it with the error is what lets a UI render the banner and both exit
    /// buttons without special-casing every endpoint it just got a 409 from.</summary>
    public OrchestratedDetail? Orchestrated { get; init; }

    public static ApiError Of(string code, string message) => new() { Code = code, Message = message };
}

public sealed record OrchestratedDetail
{
    public required string ControllerUrl { get; init; }
    public DateTimeOffset? ControlledSince { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }

    /// <summary>The two exits, always both, always available.</summary>
    public string ReleasePath { get; init; } = "release";
    public string StopPath { get; init; } = "stop";
}

public static class ApiErrorCodes
{
    /// <summary>409. The instance is under Conductor control, so this mutating call is refused. The
    /// owner keeps every read plus release and stop.</summary>
    public const string InstanceOrchestrated = "instance_orchestrated";

    public const string InstanceNotFound = "instance_not_found";
    public const string InstanceExists = "instance_exists";
    public const string InstanceRunning = "instance_running";
    public const string BuildNotFound = "build_not_found";
    public const string ContentNotFound = "content_not_found";
    public const string ContentInvalid = "content_invalid";
    public const string PortUnavailable = "port_unavailable";
    public const string ScopeDenied = "scope_denied";
    public const string Unauthorized = "unauthorized";
    public const string InvalidRequest = "invalid_request";
}

public sealed record BuildSummary
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Provider { get; init; }
    public required string PlatformKey { get; init; }
    public string? Layout { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
    public bool InUse { get; init; }
}

/// <summary>A content package in the store. Addressed by sha256 of the whole .pk3; nothing inside the
/// archive is trusted to name it.</summary>
public sealed record ContentPackage
{
    public required string Sha256 { get; init; }
    public required string Name { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Map names the package provides, from its maps/ entries.</summary>
    public IReadOnlyList<string>? Maps { get; init; }

    /// <summary>bsp or vmap. Both are accepted: the legacy format keeps working while vmap becomes
    /// the norm.</summary>
    public string? MapFormat { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>Where a runner (or a joining player) fetches it. Content-addressed, so it is cacheable
    /// and safe to hand to a CDN.</summary>
    public string? Url { get; init; }
}

public sealed record ReleaseRequest
{
    public ReleaseWhen When { get; init; } = ReleaseWhen.EndOfMatch;
    public string? Reason { get; init; }
}

public sealed record DrainRequest
{
    /// <summary>Broadcast over stdin `say` before waiting.</summary>
    public string? Message { get; init; }

    /// <summary>Give up waiting for the server to empty after this and stop anyway.</summary>
    public int TimeoutSeconds { get; init; } = 300;
}
