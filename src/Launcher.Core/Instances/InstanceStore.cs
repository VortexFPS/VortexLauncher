using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>On-disk layout for one dedicated-server instance.
///
/// instance.json is the protocol's <see cref="InstanceSpec"/> verbatim, so the file an operator edits
/// and the body the API accepts are the same document. VortexData is the server's own user directory:
/// server.cfg, the banlist and the eventlog live there and belong to the game, not to the runner.</summary>
public sealed class InstancePaths(string root)
{
    public string Root { get; } = root;
    public string SpecPath => Path.Combine(Root, "instance.json");
    public string DataDir => Path.Combine(Root, "VortexData");
    public string ConfigPath => Path.Combine(DataDir, "server.cfg");
    public string LogsDir => Path.Combine(Root, "logs");
    public string PidPath => Path.Combine(Root, "instance.pid");
    public string AuditPath => Path.Combine(Root, "audit.jsonl");

    public string LogPath(DateTimeOffset when) =>
        Path.Combine(LogsDir, $"server-{when:yyyyMMdd-HHmmss}.log");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}

/// <summary>Create, read, update and delete instances. Pure persistence: nothing here starts a process
/// or talks to a running server.</summary>
public sealed class InstanceStore(LauncherPaths paths)
{
    public LauncherPaths LauncherPaths => paths;

    public InstancePaths PathsFor(string name) =>
        new(Path.Combine(paths.InstancesDir, ValidateName(name)));

    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(paths.InstancesDir))
            return [];
        return new DirectoryInfo(paths.InstancesDir).GetDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, "instance.json")))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<InstanceSpec> List() =>
        Names().Select(Load).Where(s => s is not null).Select(s => s!).ToList();

    public InstanceSpec? Load(string name)
    {
        var file = PathsFor(name).SpecPath;
        if (!File.Exists(file))
            return null;
        try
        {
            return ManagementProtocol.Deserialize<InstanceSpec>(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(InstanceSpec spec)
    {
        var instance = PathsFor(spec.Name);
        instance.EnsureCreated();

        // temp + move, so a crash mid-write cannot leave an instance with a torn spec and no way back
        var tmp = instance.SpecPath + ".tmp";
        File.WriteAllText(tmp, ManagementProtocol.Serialize(spec));
        File.Move(tmp, instance.SpecPath, overwrite: true);
    }

    public void Delete(string name)
    {
        var instance = PathsFor(name);
        if (Directory.Exists(instance.Root))
            Directory.Delete(instance.Root, recursive: true);
    }

    public bool Exists(string name) => File.Exists(PathsFor(name).SpecPath);

    /// <summary>Instance names become directory names and command-line arguments, so they are
    /// restricted rather than sanitized. Silently rewriting an operator's name would make
    /// `vortex server start x` fail to find the thing they just created.</summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("instance name cannot be empty", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException("instance name is limited to 64 characters", nameof(name));
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                throw new ArgumentException(
                    $"instance name '{name}' may only contain letters, digits, '-', '_' and '.'",
                    nameof(name));
        if (name is "." or "..")
            throw new ArgumentException("instance name cannot be '.' or '..'", nameof(name));
        return name;
    }

    /// <summary>Write a starter server.cfg if the instance has none. Never overwrites: an operator's
    /// config is theirs, and the one setting explained below is the kind of thing that is invisible
    /// until somebody wastes an afternoon on it.</summary>
    public void EnsureDefaultConfig(InstanceSpec spec)
    {
        var instance = PathsFor(spec.Name);
        if (File.Exists(instance.ConfigPath))
            return;

        instance.EnsureCreated();
        File.WriteAllText(instance.ConfigPath, $"""
            // server.cfg for instance "{spec.Name}", executed by the game at startup (DS-5).
            // The runner does not read this file; it belongs to the game server.

            hostname "{spec.Hostname ?? spec.Name}"
            sv_maxclients {spec.MaxPlayers}

            // A true dedicated server has no local client, and bot fill is gated on
            // realPlayers > 0 || bot_join_empty. Without this the server sits empty with no bots,
            // because the v1 headless host only appeared to fill an empty map: its phantom
            // self-client was being counted as a real player.
            bot_join_empty 1

            // Structured event lines on stdout: joins, kills, votes, match boundaries and chat.
            // The runner parses these; turning it off blinds the dashboard and the chat view.
            sv_eventlog 1

            // Set a password and the runner can drive an adopted orphan whose stdin was lost.
            // rcon_password ""
            """);
    }
}

/// <summary>Port assignment for instances.
///
/// Checks the other instances' specs and then actually tries to bind, because a port free in our own
/// records can still be taken by anything else on the box. The rule from docs/RUNNING.md holds on the
/// other end too: the runner passes an explicit --port and then reads the real bind line out of
/// stdout before it reports the instance running. A process that started is not a server that
/// bound.</summary>
public sealed class PortPool(InstanceStore store, int first = 26000, int last = 26099)
{
    public int Allocate(string? forInstance = null)
    {
        var taken = store.List()
            .Where(s => forInstance is null || s.Name != forInstance)
            .Select(s => s.Port)
            .ToHashSet();

        for (var port = first; port <= last; port++)
        {
            if (taken.Contains(port) || !IsFree(port))
                continue;
            return port;
        }

        throw new InvalidOperationException(
            $"no free port in {first}-{last}; every port in the pool is assigned or in use");
    }

    /// <summary>Both protocols. The game binds UDP, but a TCP listener on the same number is a strong
    /// hint that something else owns it.</summary>
    public static bool IsFree(int port)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public bool IsAssigned(int port, string? exceptInstance = null) =>
        store.List().Any(s => s.Port == port && s.Name != exceptInstance);
}
