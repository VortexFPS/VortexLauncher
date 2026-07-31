using System.Net;
using System.Net.WebSockets;
using System.Text;
using Launcher.Protocol;
using Launcher.WebServer;

var builder = WebApplication.CreateBuilder(args);

var options = new WebServerOptions();
builder.Configuration.GetSection("WebServer").Bind(options);

// Ahead of builder.Build() on purpose: a configuration that would expose the management API never gets
// as far as constructing the server that would expose it. See BindingGate for why this refuses rather
// than warning.
var binding = BindingGate.Evaluate(options);
if (binding.Refusal is not null)
{
    Console.Error.WriteLine(binding.Refusal);
    return BindingGate.ConfigExitCode;
}

// Listening in code rather than through Urls/ASPNETCORE_URLS is deliberate: endpoints defined here
// take precedence, so no stray environment variable can quietly move this endpoint back to plaintext
// after the certificate is what allowed it off loopback in the first place.
if (binding.Certificate is not null)
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
        options.AllowRemoteBinding ? IPAddress.Any : IPAddress.Loopback,
        ManagementProtocol.DefaultWebServerPort,
        listen => listen.UseHttps(binding.Certificate)));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RunnerRegistry>();
builder.Services.AddSingleton<RunnerTokenStore>();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = ManagementProtocol.Json.PropertyNamingPolicy;
    json.SerializerOptions.DefaultIgnoreCondition = ManagementProtocol.Json.DefaultIgnoreCondition;
    foreach (var converter in ManagementProtocol.Json.Converters)
        json.SerializerOptions.Converters.Add(converter);
});

var app = builder.Build();

// The default, unchanged and needing no configuration: plain HTTP on loopback only. A certificate,
// when there is one, has already claimed the endpoint above. Remote binding without a certificate got
// past the gate by declaring a reverse proxy, and where that proxy expects to reach this process
// (a private interface, or loopback with nginx on the same box) is the operator's business, so that
// case is left to ASPNETCORE_URLS rather than guessed at here.
if (!options.AllowRemoteBinding && binding.Certificate is null)
    app.Urls.Add($"http://127.0.0.1:{ManagementProtocol.DefaultWebServerPort}");

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// Health is the only unauthenticated endpoint. Everything else, including the WebSocket upgrade,
// carries the bearer token; an upgrade that skipped auth would be a hole shaped exactly like the API.
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

// Resolved once: the store is a singleton, and the alternative is a service-locator call on every
// request for something whose whole job is to be cheap enough to do on every request.
var tokens = app.Services.GetRequiredService<RunnerTokenStore>();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path == "/healthz" || !path.StartsWith("/api", StringComparison.Ordinal)
        && path != ManagementProtocol.RunnerLinkPath)
    {
        await next();
        return;
    }

    if (!Authorized(context, tokens))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        // The remedy is named because the common cause of this is a box nobody has minted a token on
        // yet, and an operator has no way to guess where one comes from.
        await context.Response.WriteAsJsonAsync(ApiError.Of(ApiErrorCodes.Unauthorized,
            "bearer token required; `vortex runner new-token` mints one"));
        return;
    }

    await next();
});

// ---- the runner link: runners dial in here ----

app.Map(ManagementProtocol.RunnerLinkPath, async (HttpContext http, RunnerRegistry registry,
    ILoggerFactory loggers, CancellationToken ct) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var log = loggers.CreateLogger("RunnerLink");
    using var socket = await http.WebSockets.AcceptWebSocketAsync();

    var hello = await ReceiveAsync(socket, ct);
    if (hello?.Hello is null)
    {
        await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "expected hello", ct);
        return;
    }

    var runner = new LinkedRunner(hello.Hello.RunnerId, socket);
    registry.Register(runner);

    await SendAsync(socket, new PlaneFrame
    {
        Kind = PlaneFrameKind.HelloAck,
        HelloAck = new PlaneHelloAck { Accepted = true, GrantedScopes = Scopes.All },
    }, ct);

    try
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(socket, ct);
            if (frame is null)
                break;

            runner.LastSeen = DateTimeOffset.UtcNow;
            switch (frame.Kind)
            {
                case RunnerFrameKind.Status:
                    runner.Status = frame.Status;
                    break;
                case RunnerFrameKind.CommandResult when frame.Result is not null:
                    registry.Complete(frame.Result);
                    break;
                case RunnerFrameKind.LogLine when frame.Log is not null:
                    registry.Publish(frame.Log);
                    break;
                case RunnerFrameKind.ControlEvent when frame.ControlEvent is not null:
                    // The owner's own plane sees these too. It is their box, and a release they
                    // triggered from the CLI should still show up in their dashboard.
                    log.LogInformation("control event: {Kind} of {Instance} by {Initiator}",
                        frame.ControlEvent.Kind, frame.ControlEvent.InstanceName,
                        frame.ControlEvent.Initiator);
                    break;
            }
        }
    }
    finally
    {
        registry.Unregister(runner.RunnerId);
    }
});

// ---- API: everything is a proxied runner call ----

app.MapGet("/api/v1/runners", (RunnerRegistry registry) =>
    Results.Ok(registry.All.Select(r => new
    {
        r.RunnerId,
        r.ConnectedAt,
        r.LastSeen,
        instances = r.Status?.Instances.Select(i => i.Name) ?? [],
    })));

app.MapGet("/api/v1/status", (RunnerRegistry registry) =>
{
    var runner = registry.Default;
    return runner?.Status is null
        ? Results.Json(ApiError.Of("no_runner", "no runner is linked to this control plane"),
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(runner.Status);
});

// One route for every runner verb. The panel is a thin client of the runner API, which is the same
// property that lets Conductor be a proxy: anything the runner learns, both planes get for free.
app.Map("/api/v1/instances/{**path}", async (string? path, HttpContext http,
    RunnerRegistry registry, CancellationToken ct) =>
{
    var runner = ResolveRunner(http, registry);
    if (runner is null)
        return Results.Json(ApiError.Of("no_runner", "no runner is linked"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    string? body = null;
    if (http.Request.ContentLength > 0)
    {
        using var reader = new StreamReader(http.Request.Body);
        body = await reader.ReadToEndAsync(ct);
    }

    // Query string included. The runner API documents parameters on these routes (`?tail=` on logs),
    // and rebuilding the path from the route value alone drops every one of them, which a caller
    // experiences as a parameter that is accepted and ignored.
    var result = await registry.SendAsync(runner, http.Request.Method,
        $"{ManagementProtocol.ApiPrefix}/instances/{path}{http.Request.QueryString}",
        body, Actor(http), ct);

    // A 409 from the runner carries the controlling Conductor and both exits. Passing it through
    // untouched is what lets the UI render the banner without special-casing every endpoint that can
    // produce it.
    return Results.Content(result.Body ?? "", "application/json", Encoding.UTF8, result.Status);
});

app.Map("/api/v1/builds/{**path}", ProxyAsync);
app.Map("/api/v1/content/{**path}", ProxyAsync);

// Live console. Log frames flow from the runner, and command lines flow back down as exec calls, so
// the socket is a view over the same API rather than a second control path.
app.Map("/api/v1/console/{instance}", async (string instance, HttpContext http,
    RunnerRegistry registry, CancellationToken ct) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    var runner = ResolveRunner(http, registry);
    if (runner is null)
        return;

    // A WebSocket permits one send at a time, and this socket now has two writers: log lines arriving
    // from the runner on the publisher's thread, and the answer to a command typed here. Without the
    // gate they overlap the first time somebody types while the server is talking.
    using var sendGate = new SemaphoreSlim(1, 1);

    // Subscribing is reference counted inside the registry, so a second operator opening this console
    // does not double the runner's stream and closing one of the two does not blind the other.
    await using var watch = await registry.WatchLogsAsync(runner, instance,
        line => _ = SendLineAsync(socket, sendGate, line), ct);

    var buffer = new byte[4096];
    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        WebSocketReceiveResult received;
        try
        {
            received = await socket.ReceiveAsync(buffer, ct);
        }
        catch (WebSocketException) { break; }

        if (received.MessageType == WebSocketMessageType.Close)
            break;

        var command = Encoding.UTF8.GetString(buffer, 0, received.Count).Trim();
        if (command.Length == 0)
            continue;

        var result = await registry.SendAsync(runner, "POST",
            $"{ManagementProtocol.ApiPrefix}/instances/{instance}/exec",
            ManagementProtocol.Serialize(new { command }), Actor(http), ct);

        if (ExecOutcome(instance, result) is { } outcome)
            await SendLineAsync(socket, sendGate, outcome);
    }
});

app.Run();
return 0;

// ---- helpers ----

async Task<IResult> ProxyAsync(HttpContext http, RunnerRegistry registry, CancellationToken ct)
{
    var runner = ResolveRunner(http, registry);
    if (runner is null)
        return Results.Json(ApiError.Of("no_runner", "no runner is linked"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    string? body = null;
    if (http.Request.ContentLength > 0)
    {
        using var reader = new StreamReader(http.Request.Body);
        body = await reader.ReadToEndAsync(ct);
    }

    var result = await registry.SendAsync(runner, http.Request.Method,
        ManagementProtocol.ApiPrefix + http.Request.Path.Value?["/api/v1".Length..]
            + http.Request.QueryString,
        body, Actor(http), ct);

    return Results.Content(result.Body ?? "", "application/json", Encoding.UTF8, result.Status);
}

/// <summary>Write one line to a console socket, serialized against every other writer on it.</summary>
static async Task SendLineAsync(WebSocket socket, SemaphoreSlim gate, LogLine line)
{
    try
    {
        await gate.WaitAsync();
    }
    catch (ObjectDisposedException)
    {
        return; // the socket is being torn down; a log line arriving now has nowhere to go
    }

    try
    {
        if (socket.State == WebSocketState.Open)
            await socket.SendAsync(Encoding.UTF8.GetBytes(ManagementProtocol.Serialize(line)),
                WebSocketMessageType.Text, true, CancellationToken.None);
    }
    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
    finally
    {
        try { gate.Release(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>What the runner said about a command somebody typed, as a line the console can print.
///
/// Only failures. A command the runner accepted is already on screen twice over — the panel echoes
/// the line, and the server's own output comes back over this same socket — so a "sent" per command
/// would double everything an operator types. A refusal produces nothing at all without this: a
/// semicolon the runner rejects, an instance an orchestrator holds, a runner that stopped answering.
/// Silence in a console reads as a command that ran, which is the worst of the three.</summary>
static LogLine? ExecOutcome(string instance, CommandResult result)
{
    if (result.Status is >= 200 and < 300)
        return null;

    ApiError? error = null;
    try
    {
        if (result.Body is not null)
            error = ManagementProtocol.Deserialize<ApiError>(result.Body);
    }
    catch (System.Text.Json.JsonException)
    {
        // A body that is not an ApiError still has to produce a line; the status alone says enough.
    }

    return new LogLine
    {
        InstanceName = instance,
        Stream = LogStream.Runner,
        Text = error?.Message ?? $"the runner refused that command ({result.Status})",
        Timestamp = DateTimeOffset.UtcNow,
    };
}

static LinkedRunner? ResolveRunner(HttpContext http, RunnerRegistry registry) =>
    http.Request.Headers.TryGetValue("X-Runner-Id", out var id) && id.ToString() is { Length: > 0 } value
        ? registry.Find(value)
        : registry.Default;

static string Actor(HttpContext http)
{
    var session = http.Request.Headers["X-Actor"].ToString();
    var remote = http.Connection.RemoteIpAddress ?? IPAddress.Loopback;
    return string.IsNullOrEmpty(session) ? $"webserver@{remote}" : $"{session}@{remote}";
}

/// <summary>Only the hash of the token is stored, so this hashes what was presented and compares
/// digests. Nothing on this box can produce a live token back from what is on disk.
///
/// The query string is still accepted because a browser cannot set a header on a WebSocket handshake
/// or a static page load, and the panel has both. It is the reason this endpoint stays on loopback or
/// behind TLS: a token in a URL is a token in a proxy log.</summary>
static bool Authorized(HttpContext http, RunnerTokenStore tokens)
{
    var header = http.Request.Headers.Authorization.ToString();
    var presented = header.StartsWith("Bearer ", StringComparison.Ordinal)
        ? header["Bearer ".Length..]
        : http.Request.Query["token"].ToString();

    return tokens.Verify(presented);
}

static async Task<RunnerFrame?> ReceiveAsync(WebSocket socket, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    var received = new List<byte>();

    try
    {
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            received.AddRange(buffer.Take(result.Count));
        } while (!result.EndOfMessage);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
    {
        return null;
    }

    try
    {
        return ManagementProtocol.Deserialize<RunnerFrame>(
            Encoding.UTF8.GetString(received.ToArray()));
    }
    catch (System.Text.Json.JsonException)
    {
        return null;
    }
}

static async Task SendAsync(WebSocket socket, PlaneFrame frame, CancellationToken ct) =>
    await socket.SendAsync(Encoding.UTF8.GetBytes(ManagementProtocol.Serialize(frame)),
        WebSocketMessageType.Text, true, ct);
