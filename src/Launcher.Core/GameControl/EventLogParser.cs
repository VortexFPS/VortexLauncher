namespace Launcher.Core.GameControl;

/// <summary>One parsed eventlog line. The game emits these as <c>:type:field:field</c> when
/// sv_eventlog is set (GameLog.cs), and they are how the runner learns about joins, kills, match
/// boundaries and chat without asking the server anything.</summary>
public sealed record GameEvent
{
    public required string Type { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public required string Raw { get; init; }

    /// <summary>True for chat, chat_team, chat_spec and chat_minigame.
    ///
    /// Load-bearing for privacy, not just for display: a control plane without the chat-read scope has
    /// to drop these before forwarding a log stream, and it cannot do that if chat is
    /// indistinguishable from a kill line.</summary>
    public bool IsChat => Type.StartsWith("chat", StringComparison.Ordinal);

    public string? Field(int index) => index < Fields.Count ? Fields[index] : null;
}

/// <summary>Turns raw stdout into <see cref="GameEvent"/>s and tracks the match state the runner needs
/// when the host owner reaches for one of their two exits. "Was a match live and were players in it"
/// is the difference between a routine alert and a critical one, and it has to be known at the moment
/// of the action rather than reconstructed afterward.</summary>
public sealed class EventLogParser
{
    public const string ChatType = "chat";
    public const string GameStartType = "gamestart";
    public const string GameOverType = "gameover";
    public const string JoinType = "join";
    public const string PartType = "part";

    private DateTimeOffset? _matchStartedAt;

    /// <summary>A match is running. Set by :gamestart:, cleared by :gameover:.</summary>
    public bool MatchLive => _matchStartedAt is not null;

    public int? MatchElapsedSeconds => _matchStartedAt is null
        ? null
        : (int)(DateTimeOffset.UtcNow - _matchStartedAt.Value).TotalSeconds;

    /// <summary>Current map, from the most recent :gamestart:.</summary>
    public string? Map { get; private set; }
    public string? Gametype { get; private set; }

    /// <summary>Feed one line of stdout. Returns the parsed event, or null for ordinary console
    /// output, which is most of it.</summary>
    public GameEvent? Feed(string line)
    {
        var evt = Parse(line);
        if (evt is null)
            return null;

        switch (evt.Type)
        {
            case GameStartType:
                _matchStartedAt = DateTimeOffset.UtcNow;
                // ":gamestart:<gametype>_<map>:<matchid>"
                var descriptor = evt.Field(0);
                if (descriptor is not null)
                {
                    var split = descriptor.IndexOf('_');
                    if (split > 0)
                    {
                        Gametype = descriptor[..split];
                        Map = descriptor[(split + 1)..];
                    }
                }
                break;

            case GameOverType:
                _matchStartedAt = null;
                break;
        }

        return evt;
    }

    /// <summary>Reset on process exit. A restarted server is not mid-match, and carrying the old state
    /// across would make the next release look like it interrupted something.</summary>
    public void Reset()
    {
        _matchStartedAt = null;
        Map = null;
        Gametype = null;
    }

    /// <summary>":type:field:field:..." with no state kept.
    ///
    /// Splitting on ':' is what the format allows and it is lossy: chat text containing a colon splits
    /// into extra fields. Callers that want the message body should rejoin from their field index
    /// rather than trust the last element.</summary>
    public static GameEvent? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != ':')
            return null;

        var end = trimmed.IndexOf(':', 1);
        if (end <= 1)
            return null;

        var type = trimmed[1..end];
        var rest = trimmed[(end + 1)..];
        var fields = rest.Length == 0 ? [] : rest.Split(':');

        return new GameEvent { Type = type, Fields = fields, Raw = trimmed };
    }

    /// <summary>Rejoin the fields from <paramref name="from"/> onward, undoing the split for events
    /// whose last field is free text.</summary>
    public static string TextFrom(GameEvent evt, int from) =>
        from >= evt.Fields.Count ? "" : string.Join(":", evt.Fields.Skip(from));
}
