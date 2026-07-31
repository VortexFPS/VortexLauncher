using System.Diagnostics;
using Launcher.Core.Instances;
using Launcher.Protocol;

namespace Launcher.Core.Metrics;

/// <summary>The runner's Prometheus scrape target, rendered from the supervisor on demand.
///
/// Why this is hand-rolled in Core rather than prometheus-net in Launcher.Cli, which was the other
/// viable shape:
///
/// 1. The numbers are already here. <see cref="SupervisedInstance.Status"/> computes every series
///    below - players, bots, CPU percent, working set, state, restart count - and the health loop
///    refreshes them on its own cadence. A registry in Cli would be a second copy of that state, kept
///    in step by a pump that copies Core's numbers into gauges on a timer, and then a scrape would
///    report what the pump last saw rather than what the supervisor knows. Rendering straight off
///    <c>supervisor.All()</c> has no second copy and no cadence to get wrong.
///
/// 2. The package does not bring a server the runner can use. `vortex` is a console app with no
///    ASP.NET host, so prometheus-net's exposition would arrive as either KestrelMetricServer, which
///    drags the whole hosting stack into the single binary a player runs to launch the game, or
///    MetricServer over HttpListener, which on Windows means http.sys and a URL ACL for any prefix an
///    operator would actually scrape. Either way there is still a listener to bind and harden here;
///    the dependency does not remove that work, it only decides it for us. <see cref="MetricsEndpoint"/>
///    is what that work looks like when we decide it, and it is about eighty lines.
///
/// 3. Nothing exported accumulates. Every series is a level read at scrape time, which is the one
///    case where a registry buys nothing: it exists to hold counts between the event that increments
///    them and the scrape that reads them, and there are no such events here.
///    <see cref="InstanceStatus.RestartCount"/> is a genuine counter and is already accumulated by the
///    instance that restarted.
///
/// The cost is the exposition format itself, which is <see cref="PrometheusText"/>: one frozen spec,
/// three escaping rules, and a number format. Against that, Launcher.Core keeps its BCL-only rule,
/// which ArchitectureTests fails the build over, and the whole thing is unit-testable with no
/// host.</summary>
public static class RunnerMetrics
{
    /// <summary>Every series carries this prefix so a scrape from a box running other exporters is
    /// unambiguous, and so an operator can drop the whole lot with one relabel rule.</summary>
    private const string Prefix = "vortex_";

    /// <summary>Render the current scrape body.
    ///
    /// Takes no lock and blocks on nothing. A scrape must never be able to stall the supervisor, so
    /// this reads the same snapshot objects the control planes are handed and does no probing of its
    /// own: an instance that has not been probed since the last health tick reports the last known
    /// numbers, and one that has never answered reports no numbers at all.</summary>
    public static string Render(InstanceSupervisor supervisor, RunnerConfig config, string runnerId,
        DateTimeOffset startedAt, string? dataRoot = null)
    {
        var text = new PrometheusText();
        var instances = supervisor.All();

        WriteRunner(text, config, runnerId, startedAt, dataRoot, instances);

        foreach (var instance in instances)
            WriteInstance(text, instance.Status());

        return text.ToString();
    }

    private static void WriteRunner(PrometheusText text, RunnerConfig config, string runnerId,
        DateTimeOffset startedAt, string? dataRoot, IReadOnlyList<SupervisedInstance> instances)
    {
        // The identity series. Value is always 1; the information is in the labels, which is the
        // conventional shape for build and identity metadata and is what lets a dashboard join a
        // runner's series to its name without those labels riding on every other series.
        text.Declare(Prefix + "runner_info", MetricType.Gauge,
                "Runner identity. Always 1; the labels carry the information.")
            .Sample(Prefix + "runner_info", 1,
                ("runner_id", runnerId),
                ("version", Version),
                ("platform", PlatformKey.Current),
                ("hostname", Environment.MachineName));

        text.Declare(Prefix + "runner_start_time_seconds", MetricType.Gauge,
                "Unix time at which this runner process started.")
            .Sample(Prefix + "runner_start_time_seconds", startedAt.ToUnixTimeSeconds());

        text.Declare(Prefix + "runner_instances", MetricType.Gauge,
                "Instances this runner supervises, whatever their state.")
            .Sample(Prefix + "runner_instances", instances.Count);

        text.Declare(Prefix + "runner_instances_running", MetricType.Gauge,
                "Instances whose supervised process is up.")
            .Sample(Prefix + "runner_instances_running",
                instances.Count(i => i.State == InstanceState.Running));

        // Whether this box has handed control to a Conductor, which is a fact about the runner and not
        // about any one instance: a linked runner with every instance under local control is normal.
        text.Declare(Prefix + "runner_conductor_linked", MetricType.Gauge,
                "1 when this runner is offering or holding an orchestration link, 0 otherwise.")
            .Sample(Prefix + "runner_conductor_linked",
                config is { ConductorControl: true, ConductorUrl: not null } ? 1 : 0);

        text.Declare(Prefix + "runner_instances_orchestrated", MetricType.Gauge,
                "Instances currently operated by an orchestrator rather than the host owner.")
            .Sample(Prefix + "runner_instances_orchestrated",
                instances.Count(i => i.Spec.ControlMode == ControlMode.Orchestrated));

        // Disk is the runner-level resource that actually runs out: builds, content packages and logs
        // all land under the data root, and the symptom of it filling is servers that will not start.
        text.Declare(Prefix + "runner_disk_free_bytes", MetricType.Gauge,
                "Free space on the volume holding the launcher data root.")
            .SampleIfKnown(Prefix + "runner_disk_free_bytes", FreeBytes(dataRoot));

        text.Declare(Prefix + "runner_process_memory_bytes", MetricType.Gauge,
                "Working set of the runner process itself, excluding the servers it supervises.")
            .SampleIfKnown(Prefix + "runner_process_memory_bytes", ProcessMemory());
    }

    private static void WriteInstance(PrometheusText text, InstanceStatus status)
    {
        var instance = ("instance", status.Name);

        // "up" in the sense a scraper means it: the process is running AND the server answered its last
        // getinfo. A process that holds its port and has stopped responding is the failure this whole
        // supervisor exists to catch, and reporting it up would hide exactly that.
        text.Declare(Prefix + "instance_up", MetricType.Gauge,
                "1 when the instance process is running and answering getinfo.")
            .Sample(Prefix + "instance_up",
                status is { State: InstanceState.Running, Players: not null } ? 1 : 0, instance);

        // State as one series per possible value, with 1 on the current one. The alternative, an
        // integer code, forces every dashboard and every alert to carry a copy of the enum ordering,
        // and renumbering the enum then silently rewrites history.
        text.Declare(Prefix + "instance_state", MetricType.Gauge,
            "1 on the series matching the instance's current supervisor state, 0 on the others.");
        foreach (var state in Enum.GetValues<InstanceState>())
            text.Sample(Prefix + "instance_state", status.State == state ? 1 : 0,
                instance, ("state", state.ToString().ToLowerInvariant()));

        text.Declare(Prefix + "instance_orchestrated", MetricType.Gauge,
                "1 when an orchestrator holds this instance, 0 under local control.")
            .Sample(Prefix + "instance_orchestrated",
                status.ControlMode == ControlMode.Orchestrated ? 1 : 0, instance);

        // Players and bots separately, never summed. A server full of bots is not a populated server,
        // and a graph that adds them is the one that makes an empty fleet look healthy.
        text.Declare(Prefix + "instance_players", MetricType.Gauge,
                "Human players connected, from the last getinfo. Absent until the server answers.")
            .SampleIfKnown(Prefix + "instance_players", status.Players, instance);

        text.Declare(Prefix + "instance_bots", MetricType.Gauge,
                "Bots connected, from the last getinfo.")
            .SampleIfKnown(Prefix + "instance_bots", status.Bots, instance);

        text.Declare(Prefix + "instance_max_players", MetricType.Gauge,
                "Slot count, so a dashboard can render occupancy without a second source.")
            .SampleIfKnown(Prefix + "instance_max_players", status.MaxPlayers, instance);

        // Share of the whole machine rather than of one core, matching what SupervisedInstance
        // computes: a number that means the same thing on a 4-core VPS and a 64-core host.
        text.Declare(Prefix + "instance_cpu_percent", MetricType.Gauge,
                "Process CPU as a percentage of the whole machine. Absent when the OS will not report it.")
            .SampleIfKnown(Prefix + "instance_cpu_percent", status.CpuPercent, instance);

        text.Declare(Prefix + "instance_memory_bytes", MetricType.Gauge,
                "Resident working set of the server process.")
            .SampleIfKnown(Prefix + "instance_memory_bytes", status.MemoryBytes, instance);

        // The one genuine counter. It only ever increases within a runner's lifetime and resets to zero
        // when the runner restarts, which is what a counter means and what rate() already handles.
        text.Declare(Prefix + "instance_restarts_total", MetricType.Counter,
                "Unattended restarts this runner has performed for the instance.")
            .Sample(Prefix + "instance_restarts_total", status.RestartCount, instance);

        text.Declare(Prefix + "instance_match_live", MetricType.Gauge,
                "1 while a match is in progress, from the parsed event log.")
            .Sample(Prefix + "instance_match_live", status.MatchLive ? 1 : 0, instance);

        text.Declare(Prefix + "instance_start_time_seconds", MetricType.Gauge,
                "Unix time at which the current server process started.")
            .SampleIfKnown(Prefix + "instance_start_time_seconds",
                status.StartedAt?.ToUnixTimeSeconds(), instance);
    }

    private static string Version =>
        typeof(RunnerMetrics).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private static long? FreeBytes(string? dataRoot)
    {
        if (dataRoot is null)
            return null;

        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataRoot))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A container bind mount or a path on a filesystem the runtime will not describe. A missing
            // metric is the right answer; a wrong one would be worse than none.
            return null;
        }
    }

    private static long? ProcessMemory()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
