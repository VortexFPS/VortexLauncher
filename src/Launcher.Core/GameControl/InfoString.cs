namespace Launcher.Core.GameControl;

/// <summary>The classic <c>\key\value\key\value</c> infostring carried by getinfo/infoResponse and by
/// the dpmaster heartbeat. Inherited grammar; the odd rules below are the format's, not ours.</summary>
public static class InfoString
{
    /// <summary>Parse into a case-insensitive map. A trailing key with no value is kept with an empty
    /// value rather than dropped, because that is how a server with an unset cvar reports it and
    /// dropping it would look like the key was never sent.</summary>
    public static Dictionary<string, string> Parse(string infostring)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(infostring))
            return map;

        var parts = infostring.Split('\\', StringSplitOptions.None);
        // A well-formed infostring starts with the separator, so parts[0] is empty.
        for (var i = 1; i < parts.Length; i += 2)
        {
            var key = parts[i];
            if (key.Length == 0)
                continue;
            map[key] = i + 1 < parts.Length ? parts[i + 1] : "";
        }
        return map;
    }

    public static string Build(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (key.Contains('\\') || value.Contains('\\'))
                throw new ArgumentException($"infostring values cannot contain a backslash: {key}");
            sb.Append('\\').Append(key).Append('\\').Append(value);
        }
        return sb.ToString();
    }

    public static int GetInt(IReadOnlyDictionary<string, string> map, string key, int fallback = 0) =>
        map.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

    public static string? GetString(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;
}

/// <summary>What a getinfo probe told us. This doubles as the liveness check: a process that is alive
/// but not answering is not a running server, and the supervisor treats it as such.</summary>
public sealed record ServerInfo
{
    public required string Hostname { get; init; }
    public required string Map { get; init; }
    public required string Gametype { get; init; }
    public int Players { get; init; }
    public int Bots { get; init; }
    public int MaxPlayers { get; init; }
    public int Protocol { get; init; }
    public string? Version { get; init; }
    public bool PasswordProtected { get; init; }
    public required IReadOnlyDictionary<string, string> Raw { get; init; }

    public static ServerInfo FromInfoString(string infostring)
    {
        var map = InfoString.Parse(infostring);
        return new ServerInfo
        {
            Hostname = InfoString.GetString(map, "hostname") ?? "",
            Map = InfoString.GetString(map, "mapname") ?? "",
            Gametype = InfoString.GetString(map, "gametype") ?? "",
            // DP reports "clients" as everything connected and "bots" separately, so humans are the
            // difference. A filter built on the wrong one of these makes a bot-filled server look busy.
            Players = Math.Max(0, InfoString.GetInt(map, "clients") - InfoString.GetInt(map, "bots")),
            Bots = InfoString.GetInt(map, "bots"),
            MaxPlayers = InfoString.GetInt(map, "sv_maxclients"),
            Protocol = InfoString.GetInt(map, "protocol"),
            Version = InfoString.GetString(map, "gameversion") ?? InfoString.GetString(map, "version"),
            PasswordProtected = InfoString.GetInt(map, "needpass") != 0,
            Raw = map,
        };
    }
}
