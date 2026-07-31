using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Launcher.Core;
using Launcher.Core.GameControl;
using Launcher.Core.Instances;
using Launcher.Protocol;
using Xunit;

namespace Launcher.Tests;

/// <summary>Integration tests: every one of these starts a real Launcher.FakeGameServer process and
/// binds a real UDP port. Nothing here is mocked, because nothing worth checking about supervision
/// survives being mocked — restart policy, flap detection, drain, adoption and the health check are all
/// about what a live process does, and a stub that returns the right answer proves only that the stub
/// was written to agree.
///
/// The cost is that these are the tests most able to wedge a CI run, so: ports come from the OS rather
/// than a constant, every wait has a deadline that fails the test instead of hanging it, and every
/// spawned process is killed by an IDisposable that runs whether the assertions passed or not.
///
/// The fixture is installed as a build by copying its output beside the binary name a real install
/// carries. Renaming the apphost is enough: it resolves its managed dll from its own directory, not
/// from its own name.</summary>
public class SupervisorIntegrationTests : ScratchTest
{
    private const string BuildId = "0.0.0-fake";
    private const string BuildRootDir = "server";
    private const string MapName = "stormkeep";
    private const int MaxPlayers = 12;

    private readonly InstanceStore _store;
    private readonly BuildStore _builds;
    private readonly string _gameDir;
    private readonly string _executable;

    public SupervisorIntegrationTests()
    {
        _store = new InstanceStore(Paths);
        _builds = new BuildStore(Paths);
        _gameDir = Path.Combine(Paths.VersionsDir, BuildId, BuildRootDir);
        _executable = InstallFixtureAsBuild();
    }

    // ---- 1. it starts, it is running, and it is actually serving ----

    /// <summary>The baseline. If this fails, every other test in the file is describing a server that
    /// never came up rather than the behaviour it claims to cover.</summary>
    [Fact]
    public async Task A_started_instance_reaches_running_and_answers_a_probe_with_its_map_and_players()
    {
        using var harness = Supervise("eu-1", Fake(("FAKE_BOTS", "3")));

        await harness.Instance.StartAsync();
        Assert.Equal(InstanceState.Running, harness.Instance.State);
        Assert.NotNull(harness.Instance.Status().Pid);

        var info = await WaitForProbeAsync(harness.Instance, _ => true, "the first answered probe");

        Assert.Equal(MapName, info.Map);
        Assert.Equal(MaxPlayers, info.MaxPlayers);
        Assert.Equal(3, info.Bots);
        Assert.Equal(0, info.Players);
        // The hostname came out of the server.cfg the runner writes, so this is also the proof that
        // --userdir pointed the game at the instance directory and not at some inherited default.
        Assert.Equal("eu-1", info.Hostname);
        Assert.True(await harness.Instance.IsHealthyAsync());

        await harness.Instance.SendAsync("join alice");
        await harness.Instance.SendAsync("join bob");
        var busy = await WaitForProbeAsync(harness.Instance, i => i.Players == 2,
            "both joined players to appear in a probe");

        // Humans and bots are separate numbers. Reporting five players here is how a bot-filled
        // server ends up looking busy on a dashboard nobody can then trust.
        Assert.Equal(2, busy.Players);
        Assert.Equal(3, busy.Bots);
        Assert.Equal(2, harness.Instance.Status().Players);
        Assert.Equal(MapName, harness.Instance.Status().Map);
    }

    // ---- 2. alive is not the same as serving ----

    /// <summary>The distinction the health check exists for. A wedged server holds its port and its
    /// process forever; calling that healthy leaves it in the rotation, and players keep being sent to
    /// a server that will never answer them.</summary>
    [Fact]
    public async Task A_process_that_holds_its_port_but_answers_nothing_is_not_healthy()
    {
        using var harness = Supervise("wedged", Fake(("FAKE_HANG", "1")));

        await harness.Instance.StartAsync();

        // Waited for on the server's own bind line rather than by polling the port: a poll that binds
        // the port to test it can win the race against the server that is about to, and the failure
        // would look like a game that could not start.
        // Matched on the port alone, not on the full address: where the fixture binds is a FAKE_BIND
        // decision and this test is about the port being held, not about which interface holds it.
        await WaitForLineAsync(harness, line => line.Text.Contains($"bound to ")
                                             && line.Text.EndsWith($":{harness.Port}", StringComparison.Ordinal),
            "the fixture to report its bind");

        // Both halves have to hold at once, or this test passes for the wrong reason: a process that
        // died would also fail the health check, and would prove nothing about wedging.
        Assert.NotNull(harness.Instance.Status().Pid);
        Assert.False(PortPool.IsFree(harness.Port));

        Assert.False(await harness.Instance.IsHealthyAsync());

        // Still alive after the failed probe, and still holding the port.
        Assert.NotNull(harness.Instance.Status().Pid);
        Assert.False(PortPool.IsFree(harness.Port));

        // Never answered, so there is nothing to report. Zero would be a claim about the server.
        Assert.Null(harness.Instance.Status().Players);
    }

    // ---- 3. restart policy ----

    /// <summary>A crashed server that stays down is an outage nobody is paged for.</summary>
    [Fact]
    public async Task A_crash_with_a_nonzero_exit_restarts_under_on_failure()
    {
        using var harness = Supervise("crashy", Fake(("FAKE_CRASH_AFTER_MS", "400")),
            RestartPolicy.OnFailure, flapThreshold: 2);

        await harness.Instance.StartAsync();
        await WaitUntilAsync(() => harness.StartedPids.Count >= 2, "the restart after the crash");

        Assert.Equal("exit code 1", harness.Instance.LastExitReason);
        Assert.Equal(1, harness.Instance.RestartCount);
        Assert.NotEqual(harness.StartedPids[0], harness.StartedPids[1]);
    }

    /// <summary>DS-4 gives boot failures their own exit codes, and a clean exit is a decision the
    /// server made. Restarting it turns "shut down as asked" into a loop.</summary>
    [Fact]
    public async Task A_clean_exit_does_not_restart_under_on_failure()
    {
        using var harness = Supervise("polite", restartPolicy: RestartPolicy.OnFailure);

        await harness.Instance.StartAsync();
        await WaitForProbeAsync(harness.Instance, _ => true, "the server to come up before it is asked to leave");

        // Straight down the console, not through StopAsync: the supervisor has to treat this as an
        // exit it did not ask for, which is the case the policy is deciding about.
        await harness.Instance.SendAsync("quit");
        await WaitUntilAsync(() => harness.Instance.State == InstanceState.Stopped,
            "the clean exit to be noticed");

        Assert.Equal("exit code 0", harness.Instance.LastExitReason);

        // The first backoff is two seconds, so anything that was going to restart has had its chance.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Single(harness.StartedPids);
        Assert.Equal(0, harness.Instance.RestartCount);
    }

    /// <summary>Never means never. An operator who set this is doing something by hand and a restart
    /// underneath them is worse than the outage.</summary>
    [Fact]
    public async Task A_crash_never_restarts_under_the_never_policy()
    {
        using var harness = Supervise("manual", Fake(("FAKE_CRASH_AFTER_MS", "400")), RestartPolicy.Never);

        await harness.Instance.StartAsync();
        await WaitUntilAsync(() => harness.Instance.State == InstanceState.Stopped,
            "the crash to be noticed");

        Assert.Equal("exit code 1", harness.Instance.LastExitReason);

        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Single(harness.StartedPids);
        Assert.Equal(0, harness.Instance.RestartCount);
    }

    /// <summary>Always restarts a clean exit too, which is the difference from OnFailure and the whole
    /// reason both exist. A map rotation that ends by quitting has to come back.</summary>
    [Fact]
    public async Task A_clean_exit_restarts_under_the_always_policy()
    {
        using var harness = Supervise("resilient", restartPolicy: RestartPolicy.Always);

        await harness.Instance.StartAsync();
        await WaitForProbeAsync(harness.Instance, _ => true, "the server to come up before it is asked to leave");

        await harness.Instance.SendAsync("quit");
        await WaitUntilAsync(() => harness.StartedPids.Count >= 2, "the restart after a clean exit");
        await WaitUntilAsync(() => harness.Instance.State == InstanceState.Running,
            "the restarted instance to report Running");

        Assert.Equal(1, harness.Instance.RestartCount);
        Assert.NotEqual(harness.StartedPids[0], harness.StartedPids[1]);
    }

    // ---- 4. flapping ----

    /// <summary>A server that crashes on load restarts forever otherwise, and the only symptom is a log
    /// nobody is reading. Giving up in a named state is what puts it on a dashboard.</summary>
    [Fact]
    public async Task Repeated_crashes_inside_the_flap_window_stop_the_instance_as_flapping()
    {
        using var harness = Supervise("doomed", Fake(("FAKE_CRASH_AFTER_MS", "400")),
            RestartPolicy.OnFailure, flapThreshold: 3);

        await harness.Instance.StartAsync();
        await WaitUntilAsync(() => harness.Instance.State == InstanceState.Flapping,
            "the instance to give up and report Flapping", seconds: 60);

        // Three starts and two restarts: it stopped exactly at the threshold rather than one bounce
        // early or one late.
        Assert.Equal(3, harness.StartedPids.Count);
        Assert.Equal(2, harness.Instance.RestartCount);

        // Waited for rather than read: the state is set one statement before the line is emitted, so
        // a poll that catches Flapping can still be a hair ahead of the log.
        await WaitForLineAsync(harness, line => line.Text.Contains("not restarting"),
            "the runner log to say why it stopped");

        // Nothing further is scheduled: Flapping returns before a restart is counted.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(3, harness.StartedPids.Count);
        Assert.Equal(InstanceState.Flapping, harness.Instance.State);
    }

    // ---- 5. backoff ----

    /// <summary>Without a growing backoff a server that cannot bind its port respawns as fast as the
    /// box will let it, and the runner becomes the load problem it was meant to manage.</summary>
    [Fact]
    public async Task Restart_backoff_grows_rather_than_hot_looping()
    {
        using var harness = Supervise("backoff", Fake(("FAKE_CRASH_AFTER_MS", "400")),
            RestartPolicy.OnFailure, flapThreshold: 3);

        var clock = Stopwatch.StartNew();
        await harness.Instance.StartAsync();
        await WaitUntilAsync(() => harness.StartedPids.Count >= 3, "three starts", seconds: 60);
        clock.Stop();

        var backoffs = harness.Lines
            .Select(line => Regex.Match(line.Text, @"^restarting in (\d+)s \(attempt \d+\)$"))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToList();

        Assert.Equal(2, backoffs.Count);
        Assert.True(backoffs[1] > backoffs[0],
            $"the announced backoff did not grow: {string.Join(", ", backoffs)}");

        // Announcing a delay and taking one are different things. Two crashes at 2s and 4s cannot fit
        // into less than six seconds of wall clock unless the waits were skipped.
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(5.5),
            $"three starts inside {clock.Elapsed.TotalSeconds:F1}s means the backoff was not taken");
    }

    // ---- 6. adoption ----

    /// <summary>Adoption is what lets a runner be upgraded without taking down a server full of
    /// players. Failing it either orphans a live process — which then holds the port against every
    /// future start — or starts a second server on top of the first.</summary>
    [Fact]
    public async Task A_live_process_named_by_a_pidfile_is_adopted_rather_than_started_again()
    {
        var spec = NewSpec("adopted");
        _store.Save(spec);
        _store.EnsureDefaultConfig(spec);
        var paths = _store.PathsFor(spec.Name);
        paths.EnsureCreated();

        // Started outside any supervisor, standing in for a server whose runner has gone away.
        using var orphan = StartOrphan(spec.Port, paths.DataDir);
        await WaitForProbeAsync(spec.Port, info => info.Map == MapName, "the orphan to start serving");
        File.WriteAllText(paths.PidPath, orphan.Id.ToString());

        using var supervisor = new InstanceSupervisor(_store, _builds);
        supervisor.LoadAndAdopt();
        var instance = supervisor.Require(spec.Name);

        Assert.Equal(InstanceState.Running, instance.State);
        Assert.Equal(orphan.Id, instance.Status().Pid);

        // A start against an adopted instance must be a no-op. A second process would fail to bind and
        // exit, and the operator would be told their server had just crashed.
        await instance.StartAsync();
        Assert.Equal(orphan.Id, instance.Status().Pid);
        Assert.False(orphan.HasExited);

        // Adopted and still the live one: the probe is answered by the process we started ourselves.
        var info = await instance.ProbeAsync();
        Assert.NotNull(info);
        Assert.Equal(MapName, info!.Map);

        // stdin belonged to the runner that died, and no amount of adopting brings it back. This is
        // the whole reason the rcon fallback exists, and a supervisor that pretended otherwise would
        // silently drop every command sent to an adopted server.
        await Assert.ThrowsAsync<IOException>(() => instance.SendAsync("status"));
    }

    /// <summary>The other half: a pidfile outlives an unclean shutdown, and adopting whatever now
    /// holds that number would attach the runner to an unrelated process.</summary>
    [Fact]
    public async Task A_pidfile_naming_a_dead_process_is_not_adopted()
    {
        var spec = NewSpec("stale");
        _store.Save(spec);
        var paths = _store.PathsFor(spec.Name);
        paths.EnsureCreated();

        // --help prints and leaves without binding anything, so this is a pid that was a game server
        // and is not one now: the state an unclean shutdown leaves behind.
        int deadPid;
        using (var transient = StartFixture("--help"))
        {
            deadPid = transient.Id;
            await transient.WaitForExitAsync(TimeSpan.FromSeconds(20));
        }

        File.WriteAllText(paths.PidPath, deadPid.ToString());

        using var supervisor = new InstanceSupervisor(_store, _builds);
        supervisor.LoadAndAdopt();

        var instance = supervisor.Require(spec.Name);
        Assert.Equal(InstanceState.Stopped, instance.State);
        Assert.Null(instance.Status().Pid);
    }

    // ---- 7. stopping politely ----

    /// <summary>A killed server writes neither its ban list nor the tail of its eventlog, and both
    /// losses turn up later looking like data corruption rather than like a stop.</summary>
    [Fact]
    public async Task Stop_asks_the_server_to_quit_and_never_has_to_kill_it()
    {
        using var harness = Supervise("graceful");

        await harness.Instance.StartAsync();
        await WaitForProbeAsync(harness.Instance, _ => true, "the server to come up before it is stopped");
        Assert.True(File.Exists(harness.Paths.PidPath));

        var clock = Stopwatch.StartNew();
        await harness.Instance.StopAsync(TimeSpan.FromSeconds(20));
        clock.Stop();

        Assert.Equal(InstanceState.Stopped, harness.Instance.State);
        Assert.True(harness.Saw("> quit"), "the quit command never reached the child's stdin");
        Assert.False(harness.Saw("killing"), "the server had to be killed");

        // It exited on its own well inside the grace period. A server that ignored the command would
        // have burned the full twenty seconds before the kill, which is what the next test shows.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
            $"a graceful stop took {clock.Elapsed.TotalSeconds:F1}s");

        // The pidfile is what a later runner would adopt from. Leaving it behind points the next
        // runner at a pid that is now free for anything to reuse.
        Assert.False(File.Exists(harness.Paths.PidPath));
    }

    /// <summary>The fallback, and the control that gives the test above its teeth: a server that will
    /// not leave still has to be stoppable, or an update can never land.</summary>
    [Fact]
    public async Task A_server_that_ignores_stdin_is_killed_once_the_grace_period_runs_out()
    {
        using var harness = Supervise("stubborn", Fake(("FAKE_IGNORE_STDIN", "1")));

        await harness.Instance.StartAsync();
        await WaitForProbeAsync(harness.Instance, _ => true, "the server to come up before it is stopped");

        var clock = Stopwatch.StartNew();
        await harness.Instance.StopAsync(TimeSpan.FromSeconds(3));
        clock.Stop();

        // Not the full three seconds to the millisecond: a cancellation timer is allowed to be a tick
        // early, and the claim being made is "it waited", not "it waited precisely".
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.5),
            $"the grace period was not waited out: {clock.Elapsed.TotalSeconds:F1}s");
        Assert.True(harness.Saw("killing"), "the wedged server was never killed");
        Assert.Equal(InstanceState.Stopped, harness.Instance.State);
    }

    // ---- 8. drain ----

    /// <summary>Drain is the difference between an update an operator can run at any hour and one they
    /// have to schedule. Stopping while a player is still connected makes it the second thing.</summary>
    [Fact]
    public async Task Drain_warns_waits_while_a_player_is_connected_and_stops_once_empty()
    {
        using var harness = Supervise("draining");

        await harness.Instance.StartAsync();
        await harness.Instance.SendAsync("join alice");
        await WaitForProbeAsync(harness.Instance, info => info.Players == 1,
            "the joined player to appear in a probe");

        var drain = harness.Instance.DrainAsync("server going down after this match",
            TimeSpan.FromSeconds(60));

        await WaitUntilAsync(() => harness.Saw("> say server going down after this match"),
            "the drain warning to reach the server");
        Assert.Equal(InstanceState.Draining, harness.Instance.State);

        // Still draining several seconds later. A drain that stops here has kicked somebody.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(drain.IsCompleted);
        Assert.Equal(InstanceState.Draining, harness.Instance.State);
        Assert.NotNull(harness.Instance.Status().Pid);

        // And waiting for the reason it is supposed to be waiting for. Status().Players is whatever
        // the drain loop's own probe last saw, so this is the loop reporting that it looked, found a
        // player, and stayed. Without it the test cannot tell a correct wait from a stuck one.
        Assert.Equal(1, harness.Instance.Status().Players);

        await harness.Instance.SendAsync("part 1");
        await drain.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(InstanceState.Stopped, harness.Instance.State);
        Assert.False(harness.Saw("killing"), "the drained server had to be killed");
    }

    // ---- 9. eventlog ----

    /// <summary>The chat flag is what a control plane without the chat-read scope filters on before it
    /// forwards a log stream. A chat line that arrives unflagged is a privacy failure, not a display
    /// one, and it has to survive the whole path: the game's stdout, the pump, and the parser.</summary>
    [Fact]
    public async Task A_chat_line_from_the_server_arrives_flagged_as_chat()
    {
        using var harness = Supervise("chatty");

        await harness.Instance.StartAsync();
        await harness.Instance.SendAsync("join alice");
        await harness.Instance.SendAsync("chat 1 hello everyone");

        var chat = await WaitForLineAsync(harness, line => line.IsChat, "the chat line");

        Assert.Equal(LogStream.Event, chat.Stream);
        Assert.Equal(EventLogParser.ChatType, chat.EventType);
        Assert.Contains("hello everyone", chat.Text);

        // The join that preceded it is an event too, and not a chat one. Flagging by "came from the
        // eventlog" rather than by type would leak every kill and join into the chat view.
        var join = await WaitForLineAsync(harness, line => line.EventType == EventLogParser.JoinType,
            "the join line");
        Assert.False(join.IsChat);
    }

    /// <summary>Regression, and the reason the parser has a comment about it: the real server writes
    /// ":gameover" with NO trailing colon — it is the only eventlog line with nothing after the type.
    /// A parser that required the second colon returned null for it, so MatchLive never cleared, and
    /// every release from then on was reported as interrupting a live match. Nothing about that shows
    /// up in a fixture that writes ":gameover:", which is why this drives the real string end to
    /// end.</summary>
    [Fact]
    public async Task Match_state_follows_gamestart_and_the_bare_gameover_the_real_server_writes()
    {
        // Pinned here as well as end to end, so a future edit to the parser fails on the exact string
        // rather than on a fixture that happened to stop emitting it.
        var parsed = EventLogParser.Parse(":gameover");
        Assert.NotNull(parsed);
        Assert.Equal(EventLogParser.GameOverType, parsed!.Type);

        using var harness = Supervise("match");

        await harness.Instance.StartAsync();

        // +map opens a match, so the instance is live before anybody plays anything.
        await WaitUntilAsync(() => harness.Instance.MatchLive, "the opening :gamestart:");
        Assert.NotNull(harness.Instance.MatchElapsedSeconds);
        Assert.Equal(MapName, harness.Instance.Status().Map);

        await harness.Instance.SendAsync("gameover");

        var over = await WaitForLineAsync(harness, line => line.EventType == EventLogParser.GameOverType,
            "the :gameover: line");
        Assert.Equal(":gameover", over.Text);

        await WaitUntilAsync(() => !harness.Instance.MatchLive, "the match to stop being live");
        Assert.Null(harness.Instance.MatchElapsedSeconds);
    }

    // ---- harness ----

    /// <summary>One supervised instance backed by the fixture binary, plus the teardown that keeps a
    /// failed assertion from leaving a game server running on the box. SupervisedInstance.Dispose
    /// releases the runner's side but deliberately does not kill the child, so the pids are tracked
    /// here and killed by hand.</summary>
    private sealed class Harness : IDisposable
    {
        private static readonly Regex StartedPid = new(@"^started pid (\d+) ", RegexOptions.Compiled);

        private readonly ConcurrentQueue<LogLine> _lines = new();
        private readonly ConcurrentQueue<int> _pids = new();
        private readonly DateTime _createdAt = DateTime.Now.AddSeconds(-1);

        public Harness(SupervisedInstance instance, InstancePaths paths)
        {
            Instance = instance;
            Paths = paths;
            instance.LineWritten += Record;
        }

        public SupervisedInstance Instance { get; }
        public InstancePaths Paths { get; }
        public int Port => Instance.Spec.Port;

        public IReadOnlyList<LogLine> Lines => _lines.ToArray();

        /// <summary>Every pid the supervisor announced starting, one entry per start. A restart is
        /// then a second entry rather than something inferred from a state that has already moved on
        /// to the next crash.</summary>
        public IReadOnlyList<int> StartedPids => _pids.ToArray();

        public bool Saw(string fragment) =>
            Lines.Any(line => line.Text.Contains(fragment, StringComparison.Ordinal));

        private void Record(LogLine line)
        {
            _lines.Enqueue(line);
            if (line.Stream != LogStream.Runner)
                return;
            var match = StartedPid.Match(line.Text);
            if (match.Success)
                _pids.Enqueue(int.Parse(match.Groups[1].Value));
        }

        public void Dispose()
        {
            // Cancels the lifetime token first, so a restart already waiting out its backoff does not
            // spawn one more process behind us.
            try { Instance.Dispose(); }
            catch (Exception) { /* teardown */ }

            // Swept more than once, and from the pidfile as well as from the log, because cancelling
            // the token does not win every race: a restart whose backoff had already elapsed goes on
            // to spawn its process anyway, and it writes the pidfile before it announces the pid. A
            // single pass over StartedPids can therefore miss a live game server entirely, and a
            // missed one holds its port and outlives the run — which is how a CI runner wedges.
            for (var pass = 0; pass < 3; pass++)
            {
                if (pass > 0)
                    Thread.Sleep(100);

                foreach (var pid in StartedPids)
                    KillIfStillRunning(pid, _createdAt);
                if (PidFilePid() is { } stray)
                    KillIfStillRunning(stray, _createdAt);
            }
        }

        /// <summary>The pid currently named by the instance's pidfile, if any. Read after the instance
        /// was disposed — which deletes it — so anything found here was written by a start that
        /// outran the teardown.</summary>
        private int? PidFilePid()
        {
            try
            {
                return File.Exists(Paths.PidPath)
                       && int.TryParse(File.ReadAllText(Paths.PidPath).Trim(), out var pid)
                    ? pid
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>A fixture process this test started itself, standing in for one a previous runner left
    /// behind. Killed on dispose whether the test passed or not.</summary>
    private sealed class OrphanProcess(Process process) : IDisposable
    {
        public int Id { get; } = process.Id;
        public bool HasExited => process.HasExited;

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"the fixture was still running after {timeout.TotalSeconds:F0}s");
            }
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception) { /* teardown */ }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void KillIfStillRunning(int pid, DateTime notBefore)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // Pids are reused. Killing one that predates this test would be a far worse failure than
            // the leaked fixture this is cleaning up.
            if (process.HasExited || process.StartTime < notBefore)
                return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception) { /* already gone, or not ours to touch */ }
    }

    // ---- building instances ----

    private Harness Supervise(string name, IReadOnlyDictionary<string, string>? environment = null,
        RestartPolicy restartPolicy = RestartPolicy.OnFailure, int flapThreshold = 5)
    {
        var spec = NewSpec(name, environment, restartPolicy);
        _store.Save(spec);

        var instance = new SupervisedInstance(_store, _builds, spec)
        {
            FlapThreshold = flapThreshold,
            FlapWindow = TimeSpan.FromMinutes(5),
        };

        return new Harness(instance, _store.PathsFor(name));
    }

    private InstanceSpec NewSpec(string name, IReadOnlyDictionary<string, string>? environment = null,
        RestartPolicy restartPolicy = RestartPolicy.OnFailure) => new()
    {
        Name = name,
        Map = MapName,
        Port = FreeUdpPort(),
        MaxPlayers = MaxPlayers,
        BuildId = BuildId,
        RestartPolicy = restartPolicy,
        Environment = environment,
    };

    /// <summary>The FAKE_* variables reach the fixture through the instance spec, by the same route an
    /// operator's own environment would.</summary>
    private static Dictionary<string, string> Fake(params (string Key, string Value)[] variables) =>
        variables.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    /// <summary>Start the fixture with the arguments SupervisedInstance would pass, but outside any
    /// supervisor: a server whose runner has gone away.</summary>
    private OrphanProcess StartOrphan(int port, string dataDir) => StartFixture(
        "--dedicated", "--port", port.ToString(), "--userdir", dataDir, "+map", MapName);

    private OrphanProcess StartFixture(params string[] arguments)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _gameDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"the fixture at {_executable} did not start");

        // Drained rather than ignored. A full pipe blocks the writer, and a fixture stuck on a console
        // write is not the orphan this is simulating.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new OrphanProcess(process);
    }

    // ---- waiting, always with a deadline ----

    /// <summary>Poll until true or fail. Nothing in this file waits without one of these: a supervisor
    /// that never reaches the state under test has to fail the run rather than hang it.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"timed out after {seconds}s waiting for {what}");
    }

    private static Task<ServerInfo> WaitForProbeAsync(SupervisedInstance instance,
        Func<ServerInfo, bool> predicate, string what, int seconds = 30) =>
        WaitForProbeAsync(() => instance.ProbeAsync(), predicate, what, seconds);

    private static Task<ServerInfo> WaitForProbeAsync(int port, Func<ServerInfo, bool> predicate,
        string what, int seconds = 30) =>
        WaitForProbeAsync(
            () => new GameQueryClient().GetInfoAsync(new IPEndPoint(IPAddress.Loopback, port)),
            predicate, what, seconds);

    private static async Task<ServerInfo> WaitForProbeAsync(Func<Task<ServerInfo?>> probe,
        Func<ServerInfo, bool> predicate, string what, int seconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var info = await probe();
            if (info is not null && predicate(info))
                return info;
            await Task.Delay(100);
        }

        throw new TimeoutException($"timed out after {seconds}s waiting for {what}");
    }

    private static async Task<LogLine> WaitForLineAsync(Harness harness, Func<LogLine, bool> predicate,
        string what, int seconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = harness.Lines.FirstOrDefault(predicate);
            if (line is not null)
                return line;
            await Task.Delay(50);
        }

        throw new TimeoutException($"timed out after {seconds}s waiting for {what}");
    }

    /// <summary>Take an ephemeral port from the OS and hand it straight back. Ports are never
    /// hardcoded here: a fixed number collides with whatever else the box is running and turns an
    /// unrelated service into a test failure.
    ///
    /// Loopback, matching where the fixture binds — and matching it for the same reason the fixture
    /// does: binding 0.0.0.0 here made Windows Defender Firewall prompt for the test host itself.</summary>
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    // ---- installing the fixture as a build ----

    /// <summary>Put the fixture where the supervisor looks for a game and register it as a build.
    ///
    /// SupervisedInstance resolves the binary through PlatformKey.ExecutableCandidates, so the fixture
    /// has to be on disk under the name a real install carries. The whole output directory is copied
    /// because the apphost needs its managed dll and runtimeconfig beside it; only the host itself is
    /// renamed, which is enough because it finds that dll by its own directory and not by its own
    /// name.</summary>
    private string InstallFixtureAsBuild()
    {
        var relative = PlatformKey.ExecutableRelativePath(PlatformKey.Current);
        var target = Path.Combine(_gameDir, relative);
        var hostDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(hostDir);

        var apphost = OperatingSystem.IsWindows()
            ? "Launcher.FakeGameServer.exe"
            : "Launcher.FakeGameServer";

        foreach (var file in Directory.GetFiles(FixtureOutputDir()))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(hostDir,
                string.Equals(name, apphost, StringComparison.Ordinal)
                    ? Path.GetFileName(target)
                    : name);
            File.Copy(file, destination, overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _builds.Register(new BuildRecord
        {
            Id = BuildId,
            DirName = BuildId,
            Version = BuildId,
            PlatformKey = PlatformKey.Current,
            Layout = InstalledState.LayoutFat,
            Root = BuildRootDir,
            Provider = BuildProviders.Release,
            InstalledAt = DateTimeOffset.UtcNow,
        });

        return target;
    }

    /// <summary>Where the solution build left the fixture. Read off disk rather than referenced,
    /// because the test project wants the fixture's executable and not its types.</summary>
    private static string FixtureOutputDir()
    {
        var bin = Path.Combine(RepoRoot(), "tests", "Launcher.FakeGameServer", "bin");

        // The configuration this test assembly was built in, then the two it could otherwise be under.
        var here = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        foreach (var configuration in new[] { here, "Debug", "Release" }
                     .Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.Ordinal))
        {
            var dir = Path.Combine(bin, configuration!, "net8.0");
            if (File.Exists(Path.Combine(dir, "Launcher.FakeGameServer.dll")))
                return dir;
        }

        throw new InvalidOperationException(
            $"Launcher.FakeGameServer has not been built under {bin}; build the solution first");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VortexLauncher.sln")))
                return dir.FullName;

        throw new InvalidOperationException(
            $"VortexLauncher.sln not found above {AppContext.BaseDirectory}");
    }
}
