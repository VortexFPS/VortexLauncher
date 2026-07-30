using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core.GameControl;

/// <summary>Connectionless UDP queries against a running game server: the getinfo probe the supervisor
/// uses for liveness, and the same one a client uses to measure its own ping.
///
/// Needs no authentication and no cooperation from the game beyond the responder that is already in
/// production, which is what makes it the cheapest health check available.</summary>
public sealed class GameQueryClient
{
    private static readonly byte[] Oob = [0xFF, 0xFF, 0xFF, 0xFF];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Probe a server. Returns null on timeout, which the caller should read as "not
    /// answering" rather than "not running": those are different states and only the supervisor knows
    /// whether a process is still alive.</summary>
    public async Task<ServerInfo?> GetInfoAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var challenge = NewChallenge();
        var response = await ExchangeAsync(endpoint, $"getinfo {challenge}", ct);
        if (response is null)
            return null;

        var text = Encoding.UTF8.GetString(response);
        var marker = text.IndexOf("infoResponse", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        // DP sends "infoResponse\n\key\value..."; some builds omit the newline. Find the infostring by
        // its first separator rather than by a fixed offset.
        var start = text.IndexOf('\\', marker);
        if (start < 0)
            return null;

        var info = ServerInfo.FromInfoString(text[start..]);

        // The challenge is echoed back in the infostring. A reply that does not carry ours is a stale
        // datagram from a previous probe, not an answer to this one.
        var echoed = InfoString.GetString(info.Raw, "challenge");
        return echoed is null || echoed == challenge ? info : null;
    }

    /// <summary>Round-trip time to the server, measured here. This is the only place a meaningful ping
    /// can be taken: a master server measuring it would be measuring its own distance, not the
    /// player's.</summary>
    public async Task<TimeSpan?> PingAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var info = await GetInfoAsync(endpoint, ct);
        return info is null ? null : started.Elapsed;
    }

    internal async Task<byte[]?> ExchangeAsync(IPEndPoint endpoint, string payload, CancellationToken ct)
    {
        using var socket = new UdpClient(endpoint.AddressFamily);
        socket.Client.ReceiveTimeout = (int)Timeout.TotalMilliseconds;

        var packet = new byte[Oob.Length + Encoding.UTF8.GetByteCount(payload)];
        Oob.CopyTo(packet, 0);
        Encoding.UTF8.GetBytes(payload, packet.AsSpan(Oob.Length));

        await socket.SendAsync(packet, packet.Length, endpoint);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var result = await socket.ReceiveAsync(timeout.Token);
            return StripOob(result.Buffer);
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

    internal static byte[]? StripOob(byte[] datagram)
    {
        if (datagram.Length < 4 || datagram[0] != 0xFF || datagram[1] != 0xFF
            || datagram[2] != 0xFF || datagram[3] != 0xFF)
            return null;
        return datagram[4..];
    }

    /// <summary>Base64url with no padding, so the token survives an infostring round trip. A backslash
    /// or a quote in here would corrupt the reply it travels in.</summary>
    private static string NewChallenge()
    {
        var bytes = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
