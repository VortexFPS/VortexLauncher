using System.Text;

namespace Launcher.FakeGameServer;

/// <summary>One connected pretend player. Slot is the console slot an operator would kick; PlayerId is
/// the stable id the eventlog carries alongside it.
///
/// Both exist because the game uses both, and the console commands here take the slot while every
/// eventlog line emits the PlayerId — GameLog.cs writes <c>:join:&lt;playerid&gt;:&lt;entity&gt;:...</c>,
/// <c>:part:&lt;playerid&gt;</c> and <c>:chat:&lt;playerid&gt;:...</c>. They are only equal until somebody
/// parts and a later player reuses the slot, which is exactly when a test that conflated them would
/// start passing for the wrong reason.</summary>
public sealed record FakePlayer(int Slot, int PlayerId, string Name, string Address);

/// <summary>What one console command produced: text to print, and an exit code when the command was
/// one of the two that end the process.
///
/// The exit is returned rather than taken here so the caller can finish what it was doing first. An
/// rcon `quit` that exited inside the command handler would race its own reply packet.</summary>
public sealed record CommandResult(string Output, int? Exit = null)
{
    public static readonly CommandResult None = new("");
}

/// <summary>The pretend server: the state a getinfo probe reports, the console command table that
/// stdin, rcon, server.cfg and <c>+cmd</c> arguments all reach, and the eventlog it writes to stdout.
///
/// State is behind one lock because the UDP responder reads it while the stdin pump writes it.</summary>
public sealed class FakeServer
{
    private readonly object _gate = new();
    private readonly List<FakePlayer> _players = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextPlayerId = 1;
    private int _matchId;
    private bool _matchLive;

    /// <summary>The port actually bound, which is not necessarily the one asked for: --port 0 takes an
    /// ephemeral one.</summary>
    public required int Port { get; init; }

    /// <summary>Overrides the exit code of every deliberate exit, from FAKE_EXIT_CODE. A crash without
    /// it exits 1 and a quit without it exits 0.</summary>
    public int? ExitCodeOverride { get; init; }

    public string Hostname { get; private set; } = "Vortex Arena fake server";
    public string Map { get; private set; } = "stormkeep";
    public string Gametype { get; private set; } = "dm";
    public int MaxClients { get; private set; } = 16;
    public int Bots { get; private set; }
    public int Protocol { get; private set; } = 7;

    /// <summary>Deliberately not a real version, and deliberately not settable. Anything reading a
    /// server list should be able to tell at a glance that this is a fixture and not a build somebody
    /// shipped.</summary>
    public string GameVersion { get; } = "0.0.0-fake";

    public string? RconPassword { get; private set; }

    /// <summary>Wedged: alive, holding the port, answering no queries. Settable at runtime by the
    /// `hang` command as well as by FAKE_HANG, so a test can watch a healthy instance go unhealthy
    /// without restarting it.</summary>
    public bool Hang { get; set; }

    /// <summary>Completes with the process exit code once something asks the server to stop.</summary>
    public Task<int> ExitRequested => _exit.Task;

    public void RequestExit(int code) => _exit.TrySetResult(code);

    /// <summary>Console output. Always stdout, never the rcon reply: the eventlog is a log, and a
    /// supervisor that only saw the lines somebody asked for over rcon would see almost none.</summary>
    public void Log(string line) => Console.Out.WriteLine(line);

    /// <summary>Emit the opening :gamestart: if no map command already did. Called once after startup
    /// commands so that `+map stormkeep` produces one match rather than two.</summary>
    public void EnsureMatchStarted()
    {
        lock (_gate)
        {
            if (_matchId == 0)
                StartMatch();
        }
    }

    /// <summary>Run one console command.</summary>
    /// <param name="quietUnknown">Swallow the unknown-command complaint. Set when executing an
    /// operator's server.cfg, which is full of cvars this fixture has never heard of; complaining
    /// about each one buries the lines that matter.</param>
    public CommandResult Execute(string line, bool quietUnknown = false)
    {
        var (verb, rest) = Split(line);
        if (verb.Length == 0)
            return CommandResult.None;

        lock (_gate)
        {
            switch (verb.ToLowerInvariant())
            {
                case "quit":
                case "exit":
                    return new CommandResult("shutting down", ExitCodeOverride ?? 0);

                case "crash":
                {
                    var code = int.TryParse(rest, out var requested) ? requested : ExitCodeOverride ?? 1;
                    // stderr, because that is where a real fatal goes and the supervisor pumps both.
                    Console.Error.WriteLine("fatal: simulated crash (console command)");
                    return new CommandResult("", code);
                }

                case "status":
                    return new CommandResult(Status());

                case "help":
                    return new CommandResult(FakeServerOptions.Usage);

                case "say":
                    return new CommandResult($"say: {Unquote(rest)}");

                case "hang":
                    Hang = rest.Length == 0 || rest.ToLowerInvariant() is not ("0" or "false" or "off");
                    return new CommandResult(
                        Hang ? "hang on: getinfo will not be answered" : "hang off");

                case "map":
                {
                    if (rest.Length == 0)
                        return new CommandResult("map: expected a map name");
                    // A map change ends the match that was running. Skipping the :gameover would
                    // leave the parser believing a match spans the load.
                    if (_matchLive)
                        EndMatch();
                    Map = Unquote(rest);
                    StartMatch();
                    return CommandResult.None;
                }

                case "gametype":
                    if (rest.Length == 0)
                        return new CommandResult("gametype: expected a name");
                    Gametype = Unquote(rest);
                    return CommandResult.None;

                case "hostname":
                    Hostname = Unquote(rest);
                    return CommandResult.None;

                case "sv_maxclients":
                    if (!int.TryParse(Unquote(rest), out var maxClients) || maxClients < 1)
                        return new CommandResult("sv_maxclients: expected a positive number");
                    MaxClients = maxClients;
                    return CommandResult.None;

                case "bots":
                    if (!int.TryParse(Unquote(rest), out var bots) || bots < 0)
                        return new CommandResult("bots: expected a count");
                    Bots = bots;
                    return CommandResult.None;

                case "protocol":
                    if (!int.TryParse(Unquote(rest), out var protocol))
                        return new CommandResult("protocol: expected a number");
                    Protocol = protocol;
                    return CommandResult.None;

                case "rcon_password":
                {
                    var password = Unquote(rest);
                    RconPassword = password.Length == 0 ? null : password;
                    // Never echoed. It reaches this fixture over the same channels a real one uses,
                    // and a password in a captured log is a password in a captured log.
                    return new CommandResult(
                        RconPassword is null ? "rcon_password cleared" : "rcon_password set");
                }

                case "join":
                {
                    if (_players.Count + Bots >= MaxClients)
                        return new CommandResult("server is full");
                    var slot = NextSlot();
                    var player = new FakePlayer(slot, _nextPlayerId++,
                        rest.Length == 0 ? $"player{slot}" : Unquote(rest), "127.0.0.1");
                    _players.Add(player);
                    // Player id first, then the entity slot: the order GameLog.Join writes them.
                    Emit($":join:{player.PlayerId}:{player.Slot}:{player.Address}:{player.Name}");
                    return CommandResult.None;
                }

                case "part":
                {
                    if (!int.TryParse(rest, out var slot))
                        return new CommandResult("part: expected a slot number");
                    var player = _players.FirstOrDefault(p => p.Slot == slot);
                    if (player is null)
                        return new CommandResult($"no player in slot {slot}");
                    _players.Remove(player);
                    Emit($":part:{player.PlayerId}");
                    return CommandResult.None;
                }

                case "chat":
                {
                    var (slotText, text) = Split(rest);
                    if (!int.TryParse(slotText, out var slot) || text.Length == 0)
                        return new CommandResult("chat: expected <slot> <text>");
                    // Resolved rather than passed through, because the line carries a player id and
                    // inventing one for an empty slot would produce chat from a player who never
                    // joined — a line no real server can emit.
                    var speaker = _players.FirstOrDefault(p => p.Slot == slot);
                    if (speaker is null)
                        return new CommandResult($"no player in slot {slot}");
                    Emit($":chat:{speaker.PlayerId}:{text}");
                    return CommandResult.None;
                }

                case "kill":
                {
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                        return new CommandResult("kill: expected <attacker> <victim> [weapon]");
                    Emit($":kill:frag:{parts[0]}:{parts[1]}:type={(parts.Length > 2 ? parts[2] : "rocket")}");
                    return CommandResult.None;
                }

                case "gamestart":
                    if (_matchLive)
                        EndMatch();
                    StartMatch();
                    return CommandResult.None;

                case "gameover":
                    if (!_matchLive)
                        return new CommandResult("no match running");
                    EndMatch();
                    return CommandResult.None;

                default:
                    // The wording a DarkPlaces console uses, so a test can assert on it.
                    return quietUnknown ? CommandResult.None
                        : new CommandResult($"Unknown command \"{verb}\"");
            }
        }
    }

    /// <summary>The infostring an infoResponse carries. The challenge is echoed only when the probe
    /// sent one: a key with an empty value would fail the caller's echo check, which is a different
    /// failure from a server that never answered.</summary>
    public string InfoString(string? challenge)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            Pair(sb, "hostname", Hostname);
            Pair(sb, "mapname", Map);
            Pair(sb, "gametype", Gametype);
            // clients is everybody connected, bots included; the reader takes the difference.
            Pair(sb, "clients", (_players.Count + Bots).ToString());
            Pair(sb, "bots", Bots.ToString());
            Pair(sb, "sv_maxclients", MaxClients.ToString());
            Pair(sb, "protocol", Protocol.ToString());
            Pair(sb, "gameversion", GameVersion);
        }
        if (!string.IsNullOrEmpty(challenge))
            Pair(sb, "challenge", challenge);
        return sb.ToString();
    }

    /// <summary>Player rows for a statusResponse: <c>frags ping "name"</c>, one per line.</summary>
    public IReadOnlyList<string> PlayerRows()
    {
        lock (_gate)
            return _players.Select(p => $"0 25 \"{p.Name.Replace('"', '\'')}\"").ToList();
    }

    private string Status()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"hostname:  {Hostname}");
        sb.AppendLine($"map:       {Map}   gametype: {Gametype}   protocol: {Protocol}");
        sb.AppendLine($"match:     {(_matchLive ? $"live, id {_matchId}" : "not running")}");
        sb.AppendLine($"players:   {_players.Count} human + {Bots} bots / {MaxClients} max");
        sb.AppendLine($"port:      {Port}   uptime: {(int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds}s"
                      + $"   pid: {Environment.ProcessId}");
        sb.Append($"rcon:      {(RconPassword is null ? "no password set" : "enabled")}"
                  + $"   queries: {(Hang ? "hung, not answering" : "answering")}");
        foreach (var player in _players)
            sb.AppendLine().Append($"#{player.Slot} {player.Name} {player.Address}");
        return sb.ToString();
    }

    private void StartMatch()
    {
        _matchId++;
        _matchLive = true;
        Emit($":gamestart:{Gametype}_{Map}:{_matchId}");
    }

    private void EndMatch()
    {
        _matchLive = false;
        // No trailing colon. GameLog.GameOver writes ":gameover" and it is the only eventlog line with
        // no fields after the type, so it is the one shape a parser is most likely to get wrong.
        Emit(":gameover");
    }

    private void Emit(string eventLine) => Log(eventLine);

    private int NextSlot()
    {
        var slot = 1;
        while (_players.Any(p => p.Slot == slot))
            slot++;
        return slot;
    }

    private static void Pair(StringBuilder sb, string key, string value) =>
        sb.Append('\\').Append(key).Append('\\').Append(Sanitize(value));

    /// <summary>A backslash inside a value silently reframes every pair after it, turning the rest of
    /// the value into a key. The real server escapes; a fixture only has to not lie.</summary>
    private static string Sanitize(string value) => value.Replace('\\', '/');

    private static (string Head, string Tail) Split(string line)
    {
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
