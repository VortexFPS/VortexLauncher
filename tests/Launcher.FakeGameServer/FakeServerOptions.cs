using System.Net;

namespace Launcher.FakeGameServer;

/// <summary>Everything the fixture was told to be: the arguments the runner passes, the FAKE_*
/// environment variables that script misbehaviour, and the DarkPlaces-style <c>+cmd</c> arguments that
/// become console commands at startup.</summary>
public sealed class FakeServerOptions
{
    public const string Usage = """
        Launcher.FakeGameServer - a stand-in for the Vortex Arena dedicated server.

        Arguments (the set SupervisedInstance passes, plus DP-style +cmd):
          --dedicated             accepted and ignored; this fixture is only ever dedicated
          --port <n>              UDP port to bind; 0 takes an ephemeral one and reports it back
          --userdir <path>        instance data dir; server.cfg is executed from here if present
          +<cmd> [args...]        console command to run at startup, e.g. "+map stormkeep"
          --help                  this text

        Environment, where it binds:
          FAKE_BIND=<addr>        UDP bind address; defaults to 127.0.0.1. Set 0.0.0.0 for a
                                  production-shaped bind (and a host firewall prompt).

        Environment, scripted misbehaviour:
          FAKE_CRASH_AFTER_MS=<n> exit n ms after startup, with FAKE_EXIT_CODE or 1
          FAKE_EXIT_CODE=<n>      exit code for `quit` and for the scripted crash
          FAKE_HANG=1             bind the port but never answer getinfo, so liveness fails while
                                  the process stays alive; rcon keeps working
          FAKE_IGNORE_STDIN=1     drain stdin and act on none of it, so `quit` needs the kill path

        Environment, the shape of the server it pretends to be (each overrides server.cfg):
          FAKE_HOSTNAME=<text>    FAKE_GAMETYPE=<name>    FAKE_MAXCLIENTS=<n>
          FAKE_BOTS=<n>           FAKE_PROTOCOL=<n>       FAKE_RCON_PASSWORD=<pw>

        Console commands. stdin, rcon, server.cfg and +cmd all reach the same table:
          quit | crash [code] | status | help | say <text> | hang [0|1]
          map <name> | gametype <name> | hostname <text> | sv_maxclients <n> | bots <n>
          protocol <n> | rcon_password <pw>
          join [name] | part <slot> | chat <slot> <text> | kill <attacker> <victim> [weapon]
          gamestart | gameover
        """;

    public bool Help { get; private init; }
    public bool Dedicated { get; private init; }
    public int Port { get; private init; }
    public string? UserDir { get; private init; }

    /// <summary>Console commands from <c>+cmd</c> arguments, in the order they were given.</summary>
    public IReadOnlyList<string> StartupCommands { get; private init; } = [];

    /// <summary>Arguments this fixture does not know. Kept rather than rejected, because the runner
    /// forwards an instance's ExtraArgs verbatim and a fixture that refused to start on an unfamiliar
    /// flag would fail the test for the wrong reason.</summary>
    public IReadOnlyList<string> UnknownArguments { get; private init; } = [];

    /// <summary>Where the UDP socket binds.
    ///
    /// Loopback by default, and that default is a deliberate reversal. It used to bind 0.0.0.0 on the
    /// grounds that the fixture should be shaped like the real server — but the cost landed on every
    /// developer who ran <c>dotnet test</c>: Windows Defender Firewall raises a prompt for each new
    /// binary that listens on a public interface, and this suite starts a fresh server process per
    /// test. Dozens of prompts per run, all of them for a fixture that is only ever probed from the
    /// same machine (the supervisor's own getinfo goes to <c>IPAddress.Loopback</c>, and so does the
    /// nightly e2e).
    ///
    /// The fidelity that default was buying was thin: this is a stand-in that implements a contract,
    /// not a deployment, so listening on every interface never tested anything the real server does.
    /// What it did test — that the port is genuinely held, that a stale process still owns it, that
    /// the supervisor can re-probe it — is all just as true on loopback.
    ///
    /// <c>FAKE_BIND=0.0.0.0</c> puts it back for anyone who wants the wider bind.</summary>
    public IPAddress BindAddress { get; private init; } = IPAddress.Loopback;

    public int? CrashAfterMs { get; private init; }
    public int? ExitCode { get; private init; }
    public bool Hang { get; private init; }
    public bool IgnoreStdin { get; private init; }

    public string? Hostname { get; private init; }
    public string? Gametype { get; private init; }
    public int? MaxClients { get; private init; }
    public int? Bots { get; private init; }
    public int? Protocol { get; private init; }
    public string? RconPassword { get; private init; }

    /// <summary>Parse the command line and the environment. Throws <see cref="ArgumentException"/> on
    /// anything malformed the fixture would otherwise have to guess at.</summary>
    public static FakeServerOptions Parse(string[] args)
    {
        var help = false;
        var dedicated = false;
        int? port = null;
        string? userDir = null;
        var startup = new List<string>();
        var unknown = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dedicated":
                    dedicated = true;
                    break;
                case "--port":
                    port = ParsePort(Next(args, ref i, "--port"));
                    break;
                case "--userdir":
                    userDir = Next(args, ref i, "--userdir");
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    // "+map stormkeep" is one console command spelled across two arguments, so the
                    // trailing words belong to it until the next flag starts.
                    if (arg.StartsWith('+'))
                    {
                        var words = new List<string> { arg[1..] };
                        while (i + 1 < args.Length && !args[i + 1].StartsWith('+')
                                                   && !args[i + 1].StartsWith('-'))
                            words.Add(args[++i]);
                        startup.Add(string.Join(' ', words).Trim());
                    }
                    else
                    {
                        unknown.Add(arg);
                    }
                    break;
            }
        }

        if (!help && port is null)
            throw new ArgumentException("--port is required; the runner always passes it explicitly");

        return new FakeServerOptions
        {
            Help = help,
            Dedicated = dedicated,
            Port = port ?? 0,
            UserDir = userDir,
            StartupCommands = startup,
            UnknownArguments = unknown,
            BindAddress = EnvAddress("FAKE_BIND") ?? IPAddress.Loopback,
            CrashAfterMs = EnvInt("FAKE_CRASH_AFTER_MS"),
            ExitCode = EnvInt("FAKE_EXIT_CODE"),
            Hang = EnvFlag("FAKE_HANG"),
            IgnoreStdin = EnvFlag("FAKE_IGNORE_STDIN"),
            Hostname = Env("FAKE_HOSTNAME"),
            Gametype = Env("FAKE_GAMETYPE"),
            MaxClients = EnvInt("FAKE_MAXCLIENTS"),
            Bots = EnvInt("FAKE_BOTS"),
            Protocol = EnvInt("FAKE_PROTOCOL"),
            RconPassword = Env("FAKE_RCON_PASSWORD"),
        };
    }

    private static string Next(string[] args, ref int i, string flag) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{flag} needs a value");

    private static int ParsePort(string raw)
    {
        if (!int.TryParse(raw, out var port) || port is < 0 or > 65535)
            throw new ArgumentException($"--port must be 0-65535, got '{raw}'");
        return port;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>Unparseable means unparseable. A typo in FAKE_CRASH_AFTER_MS that quietly did nothing
    /// would present as "the supervisor failed to notice the crash", which is a long way from the
    /// truth.</summary>
    private static int? EnvInt(string name)
    {
        var raw = Env(name);
        if (raw is null)
            return null;
        if (!int.TryParse(raw, out var value))
            throw new ArgumentException($"{name} must be an integer, got '{raw}'");
        return value;
    }

    /// <summary>Same rule as <see cref="EnvInt"/>: a FAKE_BIND that does not parse is a mistake worth
    /// hearing about, not a silent fall back to the default on an interface nobody asked for.</summary>
    private static IPAddress? EnvAddress(string name)
    {
        var raw = Env(name);
        if (raw is null)
            return null;
        if (!IPAddress.TryParse(raw, out var address))
            throw new ArgumentException($"{name} must be an IP address, got '{raw}'");
        return address;
    }

    private static bool EnvFlag(string name) =>
        Env(name) is { } raw && raw.ToLowerInvariant() is not ("0" or "false" or "no" or "off");
}
