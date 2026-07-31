using Launcher.Protocol;
using Launcher.WebServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Launcher.Tests;

/// <summary>The control plane's own bookkeeping. Endpoint behaviour is covered by driving a real
/// runner against a real WebServer, which is an end-to-end job rather than a unit one; what is here is
/// the logic that decides where a command goes and who hears about a log line.</summary>
public class RunnerRegistryTests
{
    private static RunnerRegistry New() =>
        new(new WebServerOptions(), NullLogger<RunnerRegistry>.Instance);

    [Fact]
    public void No_runner_linked_means_no_default()
    {
        Assert.Null(New().Default);
    }

    /// <summary>The common case is one runner on this box, so the API lets callers omit the id rather
    /// than making every URL carry something an operator does not have to hand.</summary>
    [Fact]
    public void A_single_linked_runner_becomes_the_default()
    {
        var registry = New();
        var runner = new LinkedRunner("runner-1", null!);
        registry.Register(runner);

        Assert.Same(runner, registry.Default);
        Assert.Same(runner, registry.Find("runner-1"));
    }

    [Fact]
    public void An_unknown_runner_id_resolves_to_nothing()
    {
        var registry = New();
        registry.Register(new LinkedRunner("runner-1", null!));

        Assert.Null(registry.Find("runner-2"));
    }

    [Fact]
    public void Unregistering_removes_it()
    {
        var registry = New();
        registry.Register(new LinkedRunner("runner-1", null!));
        registry.Unregister("runner-1");

        Assert.Empty(registry.All);
        Assert.Null(registry.Default);
    }

    [Fact]
    public void Log_subscribers_only_see_their_own_instance()
    {
        var registry = New();
        var seen = new List<string>();
        using var _ = registry.SubscribeLogs("eu-1", line => seen.Add(line.Text));

        registry.Publish(Line("eu-1", "mine"));
        registry.Publish(Line("eu-2", "not mine"));

        Assert.Equal(["mine"], seen);
    }

    [Fact]
    public void Disposing_a_subscription_stops_delivery()
    {
        var registry = New();
        var seen = new List<string>();
        var subscription = registry.SubscribeLogs("eu-1", line => seen.Add(line.Text));

        registry.Publish(Line("eu-1", "before"));
        subscription.Dispose();
        registry.Publish(Line("eu-1", "after"));

        Assert.Equal(["before"], seen);
    }

    [Fact]
    public void Publishing_with_no_subscribers_is_harmless()
    {
        New().Publish(Line("eu-1", "nobody listening"));
    }

    /// <summary>A result for a command nobody is waiting on must not throw. It happens whenever a
    /// command times out and the runner answers afterward.</summary>
    [Fact]
    public void Completing_an_unknown_command_is_ignored()
    {
        New().Complete(new CommandResult { CommandId = "never-sent", Status = 200 });
    }

    private static LogLine Line(string instance, string text) => new()
    {
        InstanceName = instance,
        Stream = LogStream.Stdout,
        Text = text,
        Timestamp = DateTimeOffset.UtcNow,
    };
}

/// <summary>Chat carries a privacy obligation that the rest of a log stream does not, so a plane
/// without chat-read has to be able to drop it. That only works if chat is distinguishable, which is
/// what the flag on the wire is for.</summary>
public class LogFilteringTests
{
    private static LogLine Chat() => new()
    {
        InstanceName = "eu-1", Stream = LogStream.Event, Text = ":chat:3:hello",
        Timestamp = DateTimeOffset.UtcNow, EventType = "chat", IsChat = true,
    };

    private static LogLine Kill() => new()
    {
        InstanceName = "eu-1", Stream = LogStream.Event, Text = ":kill:frag:3:4",
        Timestamp = DateTimeOffset.UtcNow, EventType = "kill", IsChat = false,
    };

    [Fact]
    public void A_grant_without_chat_read_can_filter_chat_out()
    {
        IReadOnlyList<string> grant = [Scopes.View, Scopes.ControlInstances];
        var lines = new[] { Chat(), Kill() };

        var visible = lines
            .Where(l => !l.IsChat || grant.Contains(Scopes.ChatRead))
            .ToList();

        Assert.Single(visible);
        Assert.Equal("kill", visible[0].EventType);
    }

    [Fact]
    public void A_grant_with_chat_read_sees_everything()
    {
        IReadOnlyList<string> grant = [Scopes.View, Scopes.ChatRead];
        var lines = new[] { Chat(), Kill() };

        Assert.Equal(2, lines.Count(l => !l.IsChat || grant.Contains(Scopes.ChatRead)));
    }
}

/// <summary>The banner data travels inside the 409 body, which is what lets a UI render it without
/// special-casing every endpoint that can produce one.</summary>
public class OrchestratedErrorTests
{
    [Fact]
    public void The_error_carries_the_controller_and_both_exits()
    {
        var error = new ApiError
        {
            Code = ApiErrorCodes.InstanceOrchestrated,
            Message = "controlled",
            Orchestrated = new OrchestratedDetail
            {
                ControllerUrl = "https://conductor.vortexfps.org",
                ControlledSince = DateTimeOffset.UtcNow,
                GrantedScopes = Scopes.DefaultForAdoption,
            },
        };

        var round = ManagementProtocol.Deserialize<ApiError>(ManagementProtocol.Serialize(error))!;

        Assert.Equal(ApiErrorCodes.InstanceOrchestrated, round.Code);
        Assert.Equal("https://conductor.vortexfps.org", round.Orchestrated!.ControllerUrl);
        Assert.Equal("release", round.Orchestrated.ReleasePath);
        Assert.Equal("stop", round.Orchestrated.StopPath);
        Assert.Contains(Scopes.Moderate, round.Orchestrated.GrantedScopes!);
    }

    [Fact]
    public void An_ordinary_error_carries_no_orchestration_detail()
    {
        var error = ApiError.Of(ApiErrorCodes.InstanceNotFound, "no such instance");
        var round = ManagementProtocol.Deserialize<ApiError>(ManagementProtocol.Serialize(error))!;

        Assert.Null(round.Orchestrated);
    }
}

/// <summary>Frames are one envelope with a discriminator so a frame can be logged and replayed
/// without knowing which payload it carries.</summary>
public class FrameSerializationTests
{
    [Fact]
    public void A_command_frame_round_trips()
    {
        var frame = new PlaneFrame
        {
            Kind = PlaneFrameKind.Command,
            Command = new CommandEnvelope
            {
                CommandId = "abc", Method = "POST",
                Path = "/api/v1/instances/eu-1/start", ActorId = "operator",
                ClaimedScopes = [Scopes.ControlInstances],
            },
        };

        var round = ManagementProtocol.Deserialize<PlaneFrame>(
            ManagementProtocol.Serialize(frame))!;

        Assert.Equal(PlaneFrameKind.Command, round.Kind);
        Assert.Equal("/api/v1/instances/eu-1/start", round.Command!.Path);
        Assert.Equal(ManagementProtocol.Version, round.ProtocolVersion);
    }

    [Fact]
    public void A_control_event_frame_round_trips_with_its_match_state()
    {
        var frame = new RunnerFrame
        {
            Kind = RunnerFrameKind.ControlEvent,
            RunnerId = "runner-1",
            ControlEvent = new ControlEvent
            {
                EventId = "e1", RunnerId = "runner-1", InstanceName = "eu-1",
                Kind = ControlEventKind.Released, When = ReleaseWhen.Now,
                PlayersConnected = 12, MatchLive = true, MatchElapsedSeconds = 340,
                Initiator = "bryan@box", Timestamp = DateTimeOffset.UtcNow,
            },
        };

        var round = ManagementProtocol.Deserialize<RunnerFrame>(
            ManagementProtocol.Serialize(frame))!;

        Assert.Equal(12, round.ControlEvent!.PlayersConnected);
        Assert.True(round.ControlEvent.MatchLive);
        Assert.Equal(ReleaseWhen.Now, round.ControlEvent.When);
    }

    /// <summary>Enums travel as snake_case text. Integers would renumber themselves the day somebody
    /// reorders the enum, and the two ends would disagree silently.</summary>
    [Fact]
    public void Enums_travel_as_text()
    {
        var json = ManagementProtocol.Serialize(new ReleaseRequest { When = ReleaseWhen.EndOfMatch });
        Assert.Contains("end_of_match", json);
    }

    [Fact]
    public void Release_defaults_to_the_graceful_option()
    {
        Assert.Equal(ReleaseWhen.EndOfMatch, new ReleaseRequest().When);
    }
}
