using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Core.Metrics;

/// <summary>The scrape listener: one route, one method, no state.
///
/// A raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/> because on Windows the
/// latter is http.sys, which wants a URL ACL (`netsh http add urlacl`) for any prefix an operator would
/// really scrape, and a metrics endpoint that needs an elevated one-off command before it works is a
/// metrics endpoint nobody turns on. What is actually needed here is a single unauthenticated GET
/// answering with a string, and that is small enough to write exactly and to bound exactly.
///
/// It is deliberately not a general HTTP server, and the limits below are the reason it can be one file
/// rather than a dependency:
///
///   * exactly two paths answer; everything else is 404 without touching the filesystem
///   * nothing from the request is ever echoed into the response, so there is no header injection
///   * the request line and header block are capped and the read is timed out, so a peer that opens a
///     socket and dribbles bytes cannot hold a slot
///   * connections are capped and never kept alive, so a scraper that stops reading cannot accumulate
///   * bound to loopback unless the operator says otherwise, because the fleet shape of a host box is
///     not something to publish by default
///
/// Auth is deliberately absent, and the bind address is the control instead. A bearer token here would
/// have to be stored on the box for the scraper to read, which is a second credential with a second
/// rotation story to protect numbers that are already visible to anything that can reach the game
/// servers themselves. Reachability is the honest boundary for this one, which is why it defaults to
/// 127.0.0.1 and an operator scraping from elsewhere is opting in.</summary>
public sealed class MetricsEndpoint : IDisposable
{
    /// <summary>A default, not a claim. No port is guaranteed free on somebody else's box, which is
    /// why `vortex runner run --metrics-port` exists and why a bind failure is reported rather than
    /// fatal.</summary>
    public const int DefaultPort = 9877;

    private const string Path = "/metrics";
    private const int MaxRequestBytes = 8 * 1024;
    private const int MaxConcurrentConnections = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly Func<string> _render;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private CancellationTokenSource? _lifetime;

    public MetricsEndpoint(IPAddress bind, int port, Func<string> render)
    {
        _render = render;
        _listener = new TcpListener(bind, port);
    }

    /// <summary>Where it actually bound, which is not always where it was asked to: port 0 means "any
    /// free port", and a test needs to be told which one it got.</summary>
    public IPEndPoint? Endpoint => _listener.LocalEndpoint as IPEndPoint;

    /// <summary>Bind and serve until cancelled. Throws <see cref="SocketException"/> if the port is
    /// taken, which the caller reports rather than dying over: a runner that refuses to supervise
    /// servers because a metrics port is busy has its priorities backwards.</summary>
    public void Start(CancellationToken ct = default)
    {
        _listener.Start();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = AcceptLoopAsync(_lifetime.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            // Taken before the connection is handed off, so an over-limit peer is refused by closing
            // its socket immediately rather than by queueing work we have already decided not to do.
            if (!await _slots.WaitAsync(TimeSpan.Zero, ct))
            {
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReadTimeout);

                await using var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, timeout.Token);

                // The render callback reads live supervisor state, so it runs inside the connection and
                // not on a timer: a scrape reports what is true when it is asked.
                var body = request switch
                {
                    { Method: "GET" or "HEAD", Target: Path } => _render(),
                    _ => null,
                };

                var response = body is null
                    ? Response(404, "text/plain; charset=utf-8", $"not found; try {Path}\n")
                    : Response(200, PrometheusText.ContentType, body,
                        headOnly: request!.Method == "HEAD");

                await stream.WriteAsync(response, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            // A scraper that hung up, timed out or was cut off at shutdown. None of these are the
            // runner's problem and none of them may take the accept loop with them.
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Read the request line and discard headers up to the blank line, within a fixed budget.
    ///
    /// Null for anything that does not look like an HTTP request inside the budget, which is answered
    /// with a 404 rather than a parse error: this endpoint owes an unexpected caller nothing, and the
    /// less it says about what it is, the less it invites.</summary>
    private static async Task<Request?> ReadRequestLineAsync(NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                break;
            filled += read;

            var text = Encoding.ASCII.GetString(buffer, 0, filled);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;

            // The request line alone is enough. Headers are not read past this point because nothing
            // here varies on one: no auth, no content negotiation, no compression.
            var parts = text[..end].Split(' ');
            return parts.Length >= 2 ? new Request(parts[0], StripQuery(parts[1])) : null;
        }

        return null;
    }

    /// <summary>Prometheus appends nothing, but a hand-run curl or a probe often carries a query
    /// string, and answering 404 to `/metrics?x=1` would be a confusing lie.</summary>
    private static string StripQuery(string target)
    {
        var query = target.IndexOf('?');
        return query < 0 ? target : target[..query];
    }

    /// <summary>Content-Length counts the encoded bytes and not the characters, which is why the body
    /// is encoded before the head is built. An instance named in anything but ASCII would otherwise
    /// under-declare its own payload and the scraper would hang waiting for the rest.</summary>
    private static byte[] Response(int status, string contentType, string body, bool headOnly = false)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head =
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            // No keep-alive. One scrape is one connection, which means no idle sockets to account for
            // and no state to carry between requests.
            "Connection: close\r\n\r\n";

        var headBytes = Encoding.ASCII.GetBytes(head);
        if (headOnly)
            return headBytes;

        var response = new byte[headBytes.Length + payload.Length];
        headBytes.CopyTo(response, 0);
        payload.CopyTo(response, headBytes.Length);
        return response;
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        try { _listener.Stop(); } catch (SocketException) { }
        _slots.Dispose();
    }

    private sealed record Request(string Method, string Target);
}
