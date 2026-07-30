using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Cli;

/// <summary>Exit codes are part of the CLI's contract. CI drives the whole install and supervise
/// lifecycle from a shell, and "did it work" has to be answerable without parsing prose.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;

    /// <summary>Bad arguments. System.CommandLine returns this itself for parse failures.</summary>
    public const int Usage = 2;

    /// <summary>The release feed could not be reached. Distinct from a real failure because it is
    /// usually transient and never a reason to stop a player from launching what they have.</summary>
    public const int Unavailable = 3;

    public const int NotInstalled = 4;
    public const int NotFound = 5;

    /// <summary>The action is refused in the current state: an instance already running, a port in
    /// use, or an instance under Conductor control.</summary>
    public const int Conflict = 6;

    public const int VerificationFailed = 7;
}

/// <summary>Human text or one JSON document, never both and never interleaved.
///
/// Progress and status go to stderr in JSON mode so stdout stays a single parseable document. A
/// caller doing `vortex server list --json | jq` should not have to filter out a progress bar.</summary>
public sealed class Output(bool json)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Default escaping turns an apostrophe in a message into '. This output is read by
        // operators as often as by jq, and the HTML-injection case the strict encoder guards against
        // does not apply to a terminal.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public bool IsJson => json;

    /// <summary>Human-only narration. Silent under --json.</summary>
    public void Line(string text)
    {
        if (!json)
            Console.WriteLine(text);
    }

    /// <summary>Out-of-band progress. Always stderr, so it never pollutes a JSON document and never
    /// disappears when stdout is piped.</summary>
    public void Progress(string text) => Console.Error.WriteLine(text);

    public int Ok(object? payload = null, string? human = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                Wrap(true, payload, null, null), Options));
        else if (human is not null)
            Console.WriteLine(human);
        return ExitCodes.Ok;
    }

    public int Fail(string code, string message, int exit = ExitCodes.Error)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(Wrap(false, null, code, message), Options));
        else
            Console.Error.WriteLine($"error: {message}");
        return exit;
    }

    private static Dictionary<string, object?> Wrap(
        bool ok, object? payload, string? code, string? message)
    {
        var doc = new Dictionary<string, object?> { ["ok"] = ok };
        if (code is not null)
            doc["code"] = code;
        if (message is not null)
            doc["message"] = message;
        if (payload is not null)
            doc["data"] = payload;
        return doc;
    }
}
