using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Protocol;

/// <summary>Constants and serialization for the runner management protocol. Everything a runner, a
/// WebServer and Conductor must agree on lives here once.</summary>
public static class ManagementProtocol
{
    public const int Version = 1;

    public const string ApiPrefix = "/api/v1";

    /// <summary>Where a control plane accepts runner connections. Runners always dial out, including
    /// when the plane is on the same box: making the local case the one inbound exception would mean
    /// two auth models and two reconnect paths for no gain.</summary>
    public const string RunnerLinkPath = "/api/v1/runner-link";

    public const int DefaultWebServerPort = 7777;

    /// <summary>How long a runner waits for an ack on a control event before acting anyway. An owner
    /// reclaiming their own hardware must never be gated on a control plane being reachable, so this
    /// is short and the timeout is not an error.</summary>
    public const int ControlEventAckTimeoutMs = 2000;

    public const int HeartbeatIntervalSeconds = 15;

    /// <summary>A link with no traffic for this long is treated as lost. Deliberately several
    /// heartbeats, so one dropped frame is not an incident.</summary>
    public const int LinkTimeoutSeconds = 60;

    public static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}

/// <summary>The status codes the command envelope carries.
///
/// Spelled out here rather than taken from ASP.NET because the runner produces them and the runner is
/// BCL-only by rule. The values are HTTP's because the envelope is HTTP semantics tunneled over a
/// socket, and a control plane turns them straight back into a response.</summary>
public static class ProtocolStatus
{
    public const int Ok = 200;
    public const int Created = 201;
    public const int Accepted = 202;
    public const int NoContent = 204;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;

    /// <summary>The instance is orchestrated and this call mutates it. Body carries
    /// <see cref="OrchestratedDetail"/>.</summary>
    public const int Conflict = 409;

    public const int ServerError = 500;
    public const int Unavailable = 503;
    public const int Timeout = 504;
}

/// <summary>Methods the envelope uses. Same reason as <see cref="ProtocolStatus"/>.</summary>
public static class ProtocolMethods
{
    public const string Get = "GET";
    public const string Post = "POST";
    public const string Patch = "PATCH";
    public const string Delete = "DELETE";
}

/// <summary>What a grant permits. Chosen at adoption time, editable by the host operator afterward,
/// and enforced by the runner rather than by whoever is asking.</summary>
public static class Scopes
{
    public const string View = "view";
    public const string ControlInstances = "control-instances";
    public const string EditConfig = "edit-config";
    public const string Moderate = "moderate";

    /// <summary>Separate from <see cref="Moderate"/> on purpose. Reading player chat on somebody's
    /// community server is a privacy-relevant grant and must not ride along with "can restart it".</summary>
    public const string ChatRead = "chat-read";
    public const string ChatWrite = "chat-write";

    public const string ManageBuilds = "manage-builds";
    public const string UploadContent = "upload-content";
    public const string ShellConsole = "shell-console";

    public static readonly IReadOnlyList<string> All =
    [
        View, ControlInstances, EditConfig, Moderate,
        ChatRead, ChatWrite, ManageBuilds, UploadContent, ShellConsole,
    ];

    /// <summary>What an adopted community server grants unless the operator widens it. Enough to keep
    /// a server healthy, not enough to read its chat or rewrite its config.</summary>
    public static readonly IReadOnlyList<string> DefaultForAdoption = [View, ControlInstances, Moderate];
}
