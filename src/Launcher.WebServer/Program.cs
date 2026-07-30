using System.Net;
using System.Net.WebSockets;
using System.Text;
using Launcher.Protocol;
using Launcher.WebServer;

var builder = WebApplication.CreateBuilder(args);

var options = new WebServerOptions();
builder.Configuration.GetSection("WebServer").Bind(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RunnerRegistry>();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = ManagementProtocol.Json.PropertyNamingPolicy;
    json.SerializerOptions.DefaultIgnoreCondition = ManagementProtocol.Json.DefaultIgnoreCondition;
    foreach (var converter in ManagementProtocol.Json.Converters)
        json.SerializerOptions.Converters.Add(converter);
});

var app = builder.Build();

// Loopback unless told otherwise, and told otherwise means a deliberate setting plus TLS or a
// documented proxy. A management panel with a bearer token on a public interface is the failure this
// prevents, and it is the kind that is only noticed afterward.
if (!options.AllowRemoteBinding)
    app.Urls.Add($"http://127.0.0.1:{ManagementProtocol.DefaultWebServerPort}");

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// Health is the only unauthenticated endpoint. Everything else, including the WebSocket upgrade,
// carries the bearer token; an upgrade that skipped auth would be a hole shaped exactly like the API.
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path == "/healthz" || !path.StartsWith("/api", StringComparison.Ordinal)
        && path != ManagementProtocol.RunnerLinkPath)
    {
        await next();
        return;
    }

    if (!Authorized(context, options))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            ApiError.Of(ApiErrorCodes.Unauthorized, "bearer token required"));
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

    var result = await registry.SendAsync(runner, http.Request.Method,
        $"{ManagementProtocol.ApiPrefix}/instances/{path}", body, Actor(http), ct);

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

    using var subscription = registry.SubscribeLogs(instance, async line =>
    {
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(ManagementProtocol.Serialize(line)),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
    });

    await registry.SendFrameAsync(runner, new PlaneFrame
    {
        Kind = PlaneFrameKind.Subscribe,
        InstanceName = instance,
    }, ct);

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

        await registry.SendAsync(runner, "POST",
            $"{ManagementProtocol.ApiPrefix}/instances/{instance}/exec",
            ManagementProtocol.Serialize(new { command }), Actor(http), ct);
    }

    await registry.SendFrameAsync(runner, new PlaneFrame
    {
        Kind = PlaneFrameKind.Unsubscribe,
        InstanceName = instance,
    }, CancellationToken.None);
});

app.Run();
return;

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
        ManagementProtocol.ApiPrefix + http.Request.Path.Value?["/api/v1".Length..],
        body, Actor(http), ct);

    return Results.Content(result.Body ?? "", "application/json", Encoding.UTF8, result.Status);
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

static bool Authorized(HttpContext http, WebServerOptions options)
{
    if (string.IsNullOrEmpty(options.Token))
        return false;

    var header = http.Request.Headers.Authorization.ToString();
    var presented = header.StartsWith("Bearer ", StringComparison.Ordinal)
        ? header["Bearer ".Length..]
        : http.Request.Query["token"].ToString();

    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(presented.PadRight(64)[..64]),
        Encoding.UTF8.GetBytes(options.Token.PadRight(64)[..64]));
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
