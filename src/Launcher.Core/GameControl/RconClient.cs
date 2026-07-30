using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Core.GameControl;

/// <summary>Client half of the DarkPlaces rcon protocol (DS-6), byte-compatible with the server's
/// RconProtocol so stock tooling and this speak the same thing.
///
/// The runner prefers stdin over this. rcon exists for two cases stdin cannot cover: an adopted orphan
/// whose stdin was lost when the previous runner died, and `vortex server console` attaching to a
/// server this process did not start. Sending a command over loopback that could have gone down a pipe
/// adds an authentication surface for nothing.</summary>
public sealed class RconClient(IPEndPoint endpoint, string password)
{
    private static readonly byte[] Oob = [0xFF, 0xFF, 0xFF, 0xFF];
    private const string ChallengePrefix = "srcon HMAC-MD4 CHALLENGE ";
    private const string TimePrefix = "srcon HMAC-MD4 TIME ";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to keep collecting reply datagrams after the first one. Output longer than
    /// ~1200 bytes arrives as several packets and there is no terminator, so the only way to know the
    /// reply is complete is to stop hearing more.</summary>
    public TimeSpan DrainWindow { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Run a command using the challenge flow (rcon_secure 2). Two round trips: ask for a
    /// challenge, then send the command HMAC'd against it. Returns the console output.</summary>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
    {
        if (!IsCommandSafe(command))
            throw new ArgumentException(
                "command contains a control character or ';', which the server refuses even with a " +
                "valid password", nameof(command));

        using var socket = new UdpClient(endpoint.AddressFamily);
        socket.Client.ReceiveTimeout = (int)Timeout.TotalMilliseconds;

        var challenge = await GetChallengeAsync(socket, ct)
            ?? throw new TimeoutException($"no challenge from {endpoint}; is the server running?");

        await SendAsync(socket, BuildChallengeRequest(password, challenge, command));
        return await CollectOutputAsync(socket, ct);
    }

    /// <summary>Run a command using the time flow (rcon_secure 1). One round trip, at the cost of
    /// depending on the two clocks agreeing. Use <see cref="ExecuteAsync"/> unless the server is
    /// configured for this.</summary>
    public async Task<string> ExecuteTimeAsync(string command, CancellationToken ct = default)
    {
        if (!IsCommandSafe(command))
            throw new ArgumentException("command contains a control character or ';'", nameof(command));

        using var socket = new UdpClient(endpoint.AddressFamily);
        socket.Client.ReceiveTimeout = (int)Timeout.TotalMilliseconds;

        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SendAsync(socket, BuildTimeRequest(password, unixTime, command));
        return await CollectOutputAsync(socket, ct);
    }

    private async Task<string?> GetChallengeAsync(UdpClient socket, CancellationToken ct)
    {
        await SendAsync(socket, Concat(Oob, Encoding.UTF8.GetBytes("getchallenge")));

        var reply = await ReceiveAsync(socket, Timeout, ct);
        if (reply is null)
            return null;

        var text = Encoding.UTF8.GetString(reply);
        const string marker = "challenge ";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var token = text[(at + marker.Length)..].Trim('\0', '\n', '\r', ' ');
        return token.Length == 0 ? null : token;
    }

    private async Task<string> CollectOutputAsync(UdpClient socket, CancellationToken ct)
    {
        var output = new StringBuilder();
        var window = Timeout;

        while (true)
        {
            var reply = await ReceiveAsync(socket, window, ct);
            if (reply is null)
                break;

            // Reply bodies are "n" followed by console text (DP's QW rcon print).
            if (reply.Length >= 1 && reply[0] == (byte)'n')
                output.Append(Encoding.UTF8.GetString(reply, 1, reply.Length - 1));

            // First packet arrived; the rest, if any, follow quickly. There is no end marker.
            window = DrainWindow;
        }

        return output.ToString();
    }

    private async Task SendAsync(UdpClient socket, byte[] packet) =>
        await socket.SendAsync(packet, packet.Length, endpoint);

    private static async Task<byte[]?> ReceiveAsync(UdpClient socket, TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var result = await socket.ReceiveAsync(cts.Token);
            return GameQueryClient.StripOob(result.Buffer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // ---- packet construction, mirroring RconProtocol in the game repo ----

    /// <summary>"srcon HMAC-MD4 CHALLENGE " + 16 raw HMAC bytes + ' ' + "&lt;challenge&gt; &lt;command&gt;".
    /// The HMAC is keyed by the password over exactly that trailing string, and it is raw bytes rather
    /// than hex, which is why the payload is binary and cannot be built by string concatenation
    /// alone.</summary>
    public static byte[] BuildChallengeRequest(string password, string challenge, string command)
    {
        var message = $"{challenge} {command}";
        var hmac = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(message));
        return Concat(Oob, Encoding.UTF8.GetBytes(ChallengePrefix), hmac,
            Encoding.UTF8.GetBytes(" " + message));
    }

    public static byte[] BuildTimeRequest(string password, long unixTime, string command)
    {
        var message = $"{unixTime} {command}";
        var hmac = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(message));
        return Concat(Oob, Encoding.UTF8.GetBytes(TimePrefix), hmac,
            Encoding.UTF8.GetBytes(" " + message));
    }

    public static byte[] BuildInsecureRequest(string password, string command) =>
        Concat(Oob, Encoding.UTF8.GetBytes($"rcon {password} {command}"));

    /// <summary>The server refuses a command containing a control character or a semicolon even with a
    /// valid password, because both can chain commands past the parser. Checking here turns a silent
    /// no-op into an error the caller can see.</summary>
    public static bool IsCommandSafe(string command)
    {
        foreach (var ch in command)
            if ((ch > 0 && ch < ' ') || ch == ';')
                return false;
        return true;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
