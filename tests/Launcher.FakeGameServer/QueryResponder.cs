using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core.GameControl;

namespace Launcher.FakeGameServer;

/// <summary>The server half of the connectionless UDP protocols: getinfo/getstatus, and DarkPlaces
/// rcon in all three of its forms.
///
/// Written against the bytes rather than against Launcher.Core's clients on purpose. Core's
/// GameQueryClient and RconClient are what these tests exercise, and a fixture built out of the code
/// under test agrees with that code's bugs instead of catching them. Md4 is the one exception: it comes
/// from Core because it is pinned by RFC 1320 vectors on both sides, and a second hand-written copy
/// would be a second thing to get wrong.</summary>
public sealed class QueryResponder(FakeServer server, UdpClient socket)
{
    private const string ChallengePrefix = "srcon HMAC-MD4 CHALLENGE ";
    private const string TimePrefix = "srcon HMAC-MD4 TIME ";
    private const int HmacLength = 16;
    private const int MaxOutstandingChallenges = 256;

    private static readonly byte[] Oob = [0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly TimeSpan ChallengeValidity = TimeSpan.FromSeconds(30);

    /// <summary>How far the sender's clock may be from ours on the TIME flow. DarkPlaces defaults
    /// rcon_secure_maxdiff to 5 seconds.</summary>
    private static readonly TimeSpan TimeSkew = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, DateTimeOffset> _challenges = new(StringComparer.Ordinal);
    private readonly object _challengeGate = new();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // On Windows an ICMP port-unreachable for a reply we already sent surfaces as a
                // receive error on this socket. It says nothing about the socket's health, and a
                // fixture that quit here would look like a server that died mid-test.
                continue;
            }

            try
            {
                await HandleAsync(packet);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                server.Log($"malformed packet from {packet.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    private async Task HandleAsync(UdpReceiveResult packet)
    {
        var data = packet.Buffer;
        // Connectionless only. Anything without the header would be netchan traffic to a real server,
        // and this one has no netchan.
        if (data.Length < 5 || data[0] != 0xFF || data[1] != 0xFF || data[2] != 0xFF || data[3] != 0xFF)
            return;

        var from = packet.RemoteEndPoint;

        if (StartsWith(data, 4, "getinfo"))
        {
            if (server.Hang)
                return; // FAKE_HANG: the port is held, nothing answers on it
            await SendAsync(from, "infoResponse\n" + server.InfoString(TextFrom(data, 4 + 7)));
            return;
        }

        if (StartsWith(data, 4, "getstatus"))
        {
            if (server.Hang)
                return;
            var body = new StringBuilder("statusResponse\n")
                .Append(server.InfoString(TextFrom(data, 4 + 9))).Append('\n');
            foreach (var row in server.PlayerRows())
                body.Append(row).Append('\n');
            await SendAsync(from, body.ToString());
            return;
        }

        if (StartsWith(data, 4, "getchallenge"))
        {
            await SendAsync(from, $"challenge {IssueChallenge()}");
            return;
        }

        if (StartsWith(data, 4, ChallengePrefix))
        {
            await HandleSrconAsync(data, from, ChallengePrefix, ConsumeChallenge);
            return;
        }

        if (StartsWith(data, 4, TimePrefix))
        {
            await HandleSrconAsync(data, from, TimePrefix, WithinSkew);
            return;
        }

        if (StartsWith(data, 4, "rcon "))
        {
            await HandleInsecureRconAsync(data, from);
            return;
        }

        var text = TextFrom(data, 4);
        server.Log($"unhandled connectionless packet from {from}: {Clip(text)}");
    }

    /// <summary>Both srcon flows differ only in the prefix and in what the first token of the signed
    /// message has to be: a challenge we issued, or a clock reading close to ours.</summary>
    private async Task HandleSrconAsync(byte[] data, IPEndPoint from, string prefix,
        Func<string, bool> acceptToken)
    {
        if (server.RconPassword is not { } password)
        {
            server.Log($"rcon from {from} refused: no rcon_password set");
            return;
        }

        // 4 OOB + prefix + 16 raw HMAC bytes + ' ' + "<token> <command>". The HMAC is raw, so the
        // payload is binary and the offsets have to come from the bytes; decoding the datagram as
        // text first would move them.
        var at = 4 + prefix.Length;
        if (data.Length < at + HmacLength + 2 || data[at + HmacLength] != (byte)' ')
        {
            server.Log($"rcon from {from} refused: truncated {prefix.TrimEnd()} packet");
            return;
        }

        var mac = data[at..(at + HmacLength)];
        // Only NULs are trimmed. Anything else in the tail was signed, and removing it would fail a
        // packet that was valid.
        var message = Encoding.UTF8
            .GetString(data, at + HmacLength + 1, data.Length - (at + HmacLength + 1))
            .TrimEnd('\0');

        var expected = Md4.Hmac(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(message));
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
        {
            server.Log($"rcon from {from} refused: bad password or altered message");
            return;
        }

        var space = message.IndexOf(' ');
        if (space <= 0)
        {
            server.Log($"rcon from {from} refused: no command in a signed message");
            return;
        }

        if (!acceptToken(message[..space]))
        {
            server.Log($"rcon from {from} refused: stale, spent or unknown token");
            return;
        }

        await RunAndReplyAsync(message[(space + 1)..], from);
    }

    /// <summary>rcon_secure 0: the password travels in the clear. Here so the client's insecure
    /// packet builder has something to talk to, and for no other reason.</summary>
    private async Task HandleInsecureRconAsync(byte[] data, IPEndPoint from)
    {
        if (server.RconPassword is not { } password)
        {
            server.Log($"rcon from {from} refused: no rcon_password set");
            return;
        }

        var (given, command) = SplitFirst(TextFrom(data, 4 + 5));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(password)))
        {
            server.Log($"rcon from {from} refused: bad password");
            return;
        }

        await RunAndReplyAsync(command, from);
    }

    private async Task RunAndReplyAsync(string command, IPEndPoint from)
    {
        // The real server refuses these even with a valid password, because both can chain a second
        // command past the parser. A fixture that ran them would let a bug through.
        if (!IsCommandSafe(command))
        {
            server.Log($"rcon from {from} refused: '{Clip(command)}' contains ';' or a control character");
            return;
        }

        server.Log($"rcon from {from}: {command}");
        var result = server.Execute(command);

        // The QW print reply: 'n' then the console text. There is no terminator, which is why the
        // client stops on silence rather than on a marker.
        await SendAsync(from, result.Output.Length == 0 ? "n" : "n" + result.Output + "\n");

        if (result.Exit is { } code)
            server.RequestExit(code);
    }

    private async Task SendAsync(IPEndPoint to, string payload)
    {
        var packet = new byte[Oob.Length + Encoding.UTF8.GetByteCount(payload)];
        Oob.CopyTo(packet, 0);
        Encoding.UTF8.GetBytes(payload, packet.AsSpan(Oob.Length));
        try
        {
            await socket.SendAsync(packet, packet.Length, to);
        }
        catch (SocketException)
        {
            // A prober that closed its socket after one timeout is the common case, not an error.
        }
    }

    private string IssueChallenge()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        lock (_challengeGate)
        {
            Prune();
            _challenges[token] = DateTimeOffset.UtcNow;
        }
        return token;
    }

    /// <summary>Single use and time-boxed, like the real one. A replayed packet carrying a spent
    /// challenge proves nothing about the present.</summary>
    private bool ConsumeChallenge(string token)
    {
        lock (_challengeGate)
        {
            Prune();
            return _challenges.Remove(token, out var issued)
                   && DateTimeOffset.UtcNow - issued <= ChallengeValidity;
        }
    }

    /// <summary>Drop expired challenges, and cap the table. It binds 0.0.0.0 like the real server, so
    /// a getchallenge flood is something it can be pointed at; unbounded growth would make the fixture
    /// the thing that fell over.</summary>
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, issued) in _challenges.ToList())
            if (now - issued > ChallengeValidity)
                _challenges.Remove(token);

        if (_challenges.Count <= MaxOutstandingChallenges)
            return;
        foreach (var token in _challenges.OrderBy(c => c.Value)
                     .Take(_challenges.Count - MaxOutstandingChallenges).Select(c => c.Key).ToList())
            _challenges.Remove(token);
    }

    private static bool WithinSkew(string token) =>
        long.TryParse(token, out var unixTime)
        && (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTime)).Duration() <= TimeSkew;

    private static bool IsCommandSafe(string command)
    {
        foreach (var ch in command)
            if ((ch > 0 && ch < ' ') || ch == ';')
                return false;
        return true;
    }

    private static bool StartsWith(byte[] data, int offset, string ascii)
    {
        if (data.Length < offset + ascii.Length)
            return false;
        for (var i = 0; i < ascii.Length; i++)
            if (data[offset + i] != (byte)ascii[i])
                return false;
        return true;
    }

    private static string TextFrom(byte[] data, int offset) =>
        offset >= data.Length
            ? ""
            : Encoding.UTF8.GetString(data, offset, data.Length - offset).Trim('\0', '\n', '\r', ' ');

    private static (string Head, string Tail) SplitFirst(string text)
    {
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    private static string Clip(string text) =>
        text.Length <= 60 ? text : text[..60] + "...";
}
