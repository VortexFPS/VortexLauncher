using System.Net;
using System.Net.Sockets;

namespace Launcher.FakeGameServer;

/// <summary>A dedicated server that is not one.
///
/// The supervisor's contract with the game is small and entirely observable: four command-line
/// arguments, a bind line on stdout, connectionless getinfo for liveness, eventlog lines for match
/// state, stdin for commands and srcon for when stdin is gone. This implements that contract and
/// nothing else, so the supervisor's own behaviour — restart policy, flap detection, drain, adoption,
/// the health check — can be tested with no game engine anywhere near CI.
///
/// It is also the only place those behaviours can be provoked on demand. A real server crashes when it
/// crashes; this one crashes when FAKE_CRASH_AFTER_MS says so.</summary>
public static class Program
{
    /// <summary>Bad arguments, matching the CLI's convention in Launcher.Cli ExitCodes.</summary>
    private const int ExitUsage = 2;

    /// <summary>The port was not free. Its own code, because a boot failure and a crash are different
    /// things to a restart policy and a test should be able to tell them apart.</summary>
    private const int ExitBindFailed = 3;

    public static async Task<int> Main(string[] args)
    {
        // The runner reads the bind line back out of stdout before it calls the instance running, and
        // a buffered line is an invisible line. StreamWriter's default encoding is UTF-8 with no BOM,
        // which matters just as much: a BOM would land in front of the first line read.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        FakeServerOptions options;
        try
        {
            options = FakeServerOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"fake server: {ex.Message}");
            Console.Error.WriteLine(FakeServerOptions.Usage);
            return ExitUsage;
        }

        if (options.Help)
        {
            Console.WriteLine(FakeServerOptions.Usage);
            return 0;
        }

        UdpClient socket;
        try
        {
            // Loopback unless FAKE_BIND says otherwise — see FakeServerOptions.BindAddress for why
            // that default was reversed. The printed line below reports where it actually landed, so
            // it stays true either way.
            socket = new UdpClient(new IPEndPoint(options.BindAddress, options.Port));
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"fatal: cannot bind UDP {options.Port}: {ex.SocketErrorCode}");
            return ExitBindFailed;
        }

        var bound = (IPEndPoint)socket.Client.LocalEndPoint!;
        var server = new FakeServer { Port = bound.Port, ExitCodeOverride = options.ExitCode };

        Console.WriteLine("Vortex Arena dedicated server [Launcher.FakeGameServer, not the real game]");
        Console.WriteLine($"bound to {bound.Address}:{bound.Port}");
        if (options.UserDir is not null)
            Console.WriteLine($"userdir {options.UserDir}");
        // Said out loud rather than assumed: a real server without this opens a window and waits for a
        // player, and a runner that stopped passing it would otherwise fail somewhere much later.
        if (!options.Dedicated)
            Console.WriteLine("note: started without --dedicated; the real server would not be headless");
        if (options.UnknownArguments.Count > 0)
            Console.WriteLine($"ignoring unknown arguments: {string.Join(' ', options.UnknownArguments)}");

        // server.cfg, then the environment, then +cmd. The game runs its config before its +commands
        // and the runner's `+map` has to win; the FAKE_* overrides sit between the two because they
        // are the test harness speaking, and a harness that could not override an operator's config
        // would have to write one for every case.
        if (options.UserDir is not null)
            ExecuteConfig(server, options.UserDir);
        ApplyEnvironment(server, options);
        foreach (var command in options.StartupCommands)
            Run(server, command);

        server.EnsureMatchStarted();
        server.Hang |= options.Hang;

        if (options.Hang)
            Console.WriteLine("FAKE_HANG: holding the port, answering no queries; rcon still works");
        if (options.IgnoreStdin)
            Console.WriteLine("FAKE_IGNORE_STDIN: stdin is drained and acted on by nothing");

        using var lifetime = new CancellationTokenSource();
        var responder = new QueryResponder(server, socket);
        _ = responder.RunAsync(lifetime.Token);

        StartStdinPump(server, options.IgnoreStdin);
        if (options.CrashAfterMs is { } delay)
            StartCrashTimer(server, delay, options.ExitCode ?? 1, lifetime.Token);

        var code = await server.ExitRequested;
        await lifetime.CancelAsync();
        socket.Dispose();
        return code;
    }

    /// <summary>Execute server.cfg the way the game would, because that is all the file is: a list of
    /// console commands. The runner writes hostname and sv_maxclients into it, and an operator's
    /// rcon_password is the reason the srcon path has a password to check at all.</summary>
    private static void ExecuteConfig(FakeServer server, string userDir)
    {
        var path = Path.Combine(userDir, "server.cfg");
        if (!File.Exists(path))
            return;

        foreach (var raw in File.ReadLines(path))
        {
            // Comments are stripped here and not in the command table: "say see http://example" is a
            // legitimate console command and must not lose its tail to a // that was never a comment.
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];

            line = line.Trim();
            if (line.Length > 0)
                Run(server, line, quietUnknown: true);
        }
    }

    private static void ApplyEnvironment(FakeServer server, FakeServerOptions options)
    {
        // Routed through the console rather than assigned, so there is exactly one place where each
        // of these is validated and exactly one place where it takes effect.
        if (options.Hostname is { } hostname)
            Run(server, $"hostname {hostname}");
        if (options.Gametype is { } gametype)
            Run(server, $"gametype {gametype}");
        if (options.MaxClients is { } maxClients)
            Run(server, $"sv_maxclients {maxClients}");
        if (options.Bots is { } bots)
            Run(server, $"bots {bots}");
        if (options.Protocol is { } protocol)
            Run(server, $"protocol {protocol}");
        if (options.RconPassword is { } password)
            Run(server, $"rcon_password {password}");
    }

    private static void Run(FakeServer server, string command, bool quietUnknown = false)
    {
        var result = server.Execute(command, quietUnknown);
        if (result.Output.Length > 0)
            server.Log(result.Output);
        if (result.Exit is { } code)
            server.RequestExit(code);
    }

    /// <summary>The primary command channel, and the one with no network surface. Runs on a pool
    /// thread because Console.In has no cancellable read: the process exits out from under it.</summary>
    private static void StartStdinPump(FakeServer server, bool ignore) => _ = Task.Run(() =>
    {
        using var stdin = new StreamReader(Console.OpenStandardInput());
        while (true)
        {
            string? line;
            try
            {
                line = stdin.ReadLine();
            }
            catch (IOException)
            {
                break;
            }

            if (line is null)
                break;
            // FAKE_IGNORE_STDIN still reads. An undrained pipe blocks the writer once its buffer
            // fills, and a supervisor stuck writing "quit" is not the failure being simulated.
            if (!ignore)
                Run(server, line);
        }

        // stdin closed: whoever owned it is gone. A real server keeps running and can only be driven
        // over rcon from here, which is exactly the adopted-orphan case the supervisor has to cover.
        // Unless it is closing because we are already on our way out, when saying so would be a lie.
        if (!server.ExitRequested.IsCompleted)
            server.Log($"stdin closed; still serving on {server.Port}, rcon only");
    });

    private static void StartCrashTimer(FakeServer server, int afterMs, int code, CancellationToken ct)
        => _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(afterMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Console.Error.WriteLine($"fatal: scripted crash {afterMs}ms after start (FAKE_CRASH_AFTER_MS)");
            server.RequestExit(code);
        }, ct);
}
