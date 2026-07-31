using System.Net;
using System.Net.Sockets;
using System.Text;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Core.Metrics;
using Launcher.Protocol;
using Xunit;

namespace Launcher.Tests;

/// <summary>The exposition format itself. These are the rules a scraper enforces by rejecting the
/// whole payload rather than the offending line, which is why they are worth pinning: one unescaped
/// quote in an instance name loses every series on the box, not one.</summary>
public class PrometheusTextTests
{
    [Fact]
    public void Help_and_type_are_written_once_per_family()
    {
        var text = new PrometheusText();
        text.Declare("vortex_x", MetricType.Gauge, "help").Sample("vortex_x", 1, ("a", "1"));
        text.Declare("vortex_x", MetricType.Gauge, "help").Sample("vortex_x", 2, ("a", "2"));

        var rendered = text.ToString();

        // Redeclaring a family is a payload-level error, and the per-instance loops in RunnerMetrics
        // declare inside the loop on purpose so the call site stays readable.
        Assert.Equal(1, Occurrences(rendered, "# HELP vortex_x"));
        Assert.Equal(1, Occurrences(rendered, "# TYPE vortex_x"));
        Assert.Contains("vortex_x{a=\"1\"} 1\n", rendered);
        Assert.Contains("vortex_x{a=\"2\"} 2\n", rendered);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("two\nlines", "two\\nlines")]
    public void Label_values_escape_the_three_characters_that_terminate_them(string raw,
        string expected) =>
        Assert.Equal(expected, PrometheusText.EscapeLabel(raw));

    [Fact]
    public void An_instance_named_with_a_quote_does_not_corrupt_the_series_after_it()
    {
        var text = new PrometheusText();
        text.Declare("vortex_x", MetricType.Gauge, "help")
            .Sample("vortex_x", 1, ("instance", "eu\"1"))
            .Sample("vortex_x", 2, ("instance", "eu-2"));

        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Two comment lines and exactly two samples: the escaped quote stayed inside its own label.
        Assert.Equal(4, lines.Length);
        Assert.Contains(@"vortex_x{instance=""eu\""1""} 1", lines);
    }

    [Fact]
    public void A_fractional_value_uses_a_point_and_not_the_machine_locale()
    {
        var text = new PrometheusText();
        text.Declare("vortex_cpu", MetricType.Gauge, "help").Sample("vortex_cpu", 12.5);

        // The failure this guards is a runner on a de-DE box emitting "12,5", which a scraper reads as
        // two fields and rejects the payload over.
        Assert.Contains("vortex_cpu 12.5\n", text.ToString());
        Assert.DoesNotContain(",", text.ToString());
    }

    [Fact]
    public void An_unknown_value_is_omitted_rather_than_written_as_zero()
    {
        var text = new PrometheusText();
        text.Declare("vortex_players", MetricType.Gauge, "help")
            .SampleIfKnown("vortex_players", null, ("instance", "eu-1"))
            .SampleIfKnown("vortex_players", 0, ("instance", "eu-2"));

        var rendered = text.ToString();

        // Absent is not zero. A server that has not answered yet is not an empty server, and drawing
        // it as one is the difference between a gap in a graph and a false alarm.
        Assert.DoesNotContain("eu-1", rendered);
        Assert.Contains("vortex_players{instance=\"eu-2\"} 0\n", rendered);
    }

    [Fact]
    public void Non_finite_values_use_the_names_the_format_defines() =>
        Assert.Contains("vortex_x NaN\n",
            new PrometheusText().Sample("vortex_x", double.NaN).ToString());

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>What the runner actually renders, off a real supervisor.</summary>
public class RunnerMetricsTests : ScratchTest
{
    private InstanceSupervisor Supervisor(params InstanceSpec[] specs)
    {
        var paths = Paths;
        var supervisor = new InstanceSupervisor(new InstanceStore(paths), new BuildStore(paths));
        foreach (var spec in specs)
            supervisor.Create(spec);
        return supervisor;
    }

    private static string Render(InstanceSupervisor supervisor, RunnerConfig? config = null) =>
        RunnerMetrics.Render(supervisor, config ?? new RunnerConfig(), "runner-1",
            DateTimeOffset.UnixEpoch.AddSeconds(1000));

    [Fact]
    public void Exactly_one_state_series_is_set_for_each_instance()
    {
        using var supervisor = Supervisor(new InstanceSpec { Name = "eu-1", Map = "m", Port = 26000 });

        var lines = Render(supervisor).Split('\n');
        var stateLines = lines.Where(l => l.StartsWith("vortex_instance_state{", StringComparison.Ordinal))
            .ToList();

        // One series per enum value, so a dashboard never has to carry a copy of the enum ordering.
        Assert.Equal(Enum.GetValues<InstanceState>().Length, stateLines.Count);
        Assert.Single(stateLines, l => l.EndsWith(" 1", StringComparison.Ordinal));
        Assert.Contains(stateLines, l => l.Contains("state=\"stopped\"") &&
                                         l.EndsWith(" 1", StringComparison.Ordinal));
    }

    [Fact]
    public void An_instance_that_has_never_answered_reports_no_player_count()
    {
        using var supervisor = Supervisor(new InstanceSpec { Name = "eu-1", Map = "m", Port = 26000 });

        var rendered = Render(supervisor);

        // The declaration is present so the family exists; the sample is not, because nothing is known.
        Assert.Contains("# TYPE vortex_instance_players gauge", rendered);
        Assert.DoesNotContain("vortex_instance_players{", rendered);
        Assert.Contains("vortex_instance_up{instance=\"eu-1\"} 0\n", rendered);
    }

    [Fact]
    public void Runner_level_series_count_the_instances_and_report_the_link()
    {
        using var supervisor = Supervisor(
            new InstanceSpec { Name = "eu-1", Map = "m", Port = 26000 },
            new InstanceSpec { Name = "eu-2", Map = "m", Port = 26001 });

        var linked = new RunnerConfig
        {
            ConductorControl = true,
            ConductorUrl = "https://conductor.example",
        };

        var rendered = Render(supervisor, linked);

        Assert.Contains("vortex_runner_instances 2\n", rendered);
        Assert.Contains("vortex_runner_instances_running 0\n", rendered);
        Assert.Contains("vortex_runner_conductor_linked 1\n", rendered);
        Assert.Contains("runner_id=\"runner-1\"", rendered);
    }

    [Fact]
    public void An_address_without_the_opt_in_is_not_a_link()
    {
        using var supervisor = Supervisor();

        // Both halves have to be true, the same rule `runner run` applies when deciding whether to
        // dial: a url left behind by an unlink is not an offer.
        var rendered = Render(supervisor,
            new RunnerConfig { ConductorControl = false, ConductorUrl = "https://conductor.example" });

        Assert.Contains("vortex_runner_conductor_linked 0\n", rendered);
    }

    [Fact]
    public void Restart_count_is_exported_as_a_counter()
    {
        using var supervisor = Supervisor(new InstanceSpec { Name = "eu-1", Map = "m", Port = 26000 });

        var rendered = Render(supervisor);

        Assert.Contains("# TYPE vortex_instance_restarts_total counter", rendered);
        Assert.Contains("vortex_instance_restarts_total{instance=\"eu-1\"} 0\n", rendered);
    }
}

/// <summary>The listener. Hand-written HTTP, so the parts worth asserting are the ones a real server
/// would have given us: the route, the method, and refusing everything else without touching disk.</summary>
public class MetricsEndpointTests
{
    private static async Task<string> RequestAsync(IPEndPoint endpoint, string requestLine)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine}\r\nHost: localhost\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static MetricsEndpoint Listening(Func<string> render)
    {
        // Port 0: the OS picks a free one, so these tests never collide with each other or with
        // whatever else is on the box.
        var endpoint = new MetricsEndpoint(IPAddress.Loopback, 0, render);
        endpoint.Start();
        return endpoint;
    }

    [Fact]
    public async Task A_scrape_gets_the_rendered_body()
    {
        using var endpoint = Listening(() => "vortex_x 1\n");

        var response = await RequestAsync(endpoint.Endpoint!, "GET /metrics HTTP/1.1");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Type: text/plain; version=0.0.4", response);
        Assert.EndsWith("vortex_x 1\n", response);
    }

    [Fact]
    public async Task The_body_is_rendered_per_request_and_not_cached()
    {
        var scrapes = 0;
        using var endpoint = Listening(() => $"vortex_scrapes {++scrapes}\n");

        await RequestAsync(endpoint.Endpoint!, "GET /metrics HTTP/1.1");
        var second = await RequestAsync(endpoint.Endpoint!, "GET /metrics HTTP/1.1");

        // A scrape has to report what is true when it is asked, which is the whole reason the render
        // callback reads the supervisor rather than a snapshot some pump refreshed on a timer.
        Assert.EndsWith("vortex_scrapes 2\n", second);
    }

    [Fact]
    public async Task A_query_string_still_reaches_the_metrics_route()
    {
        using var endpoint = Listening(() => "vortex_x 1\n");

        var response = await RequestAsync(endpoint.Endpoint!, "GET /metrics?debug=1 HTTP/1.1");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1")]
    [InlineData("GET /../../etc/passwd HTTP/1.1")]
    [InlineData("POST /metrics HTTP/1.1")]
    [InlineData("garbage")]
    public async Task Everything_that_is_not_a_metrics_read_is_a_flat_404(string requestLine)
    {
        var rendered = false;
        using var endpoint = Listening(() => { rendered = true; return "vortex_x 1\n"; });

        var response = await RequestAsync(endpoint.Endpoint!, requestLine);

        Assert.StartsWith("HTTP/1.1 404", response);
        // Nothing about the request reaches the response, and the renderer is never reached at all.
        Assert.False(rendered);
    }

    [Fact]
    public async Task A_head_request_carries_the_length_and_no_body()
    {
        using var endpoint = Listening(() => "vortex_x 1\n");

        var response = await RequestAsync(endpoint.Endpoint!, "HEAD /metrics HTTP/1.1");

        Assert.Contains("Content-Length: 11", response);
        Assert.DoesNotContain("vortex_x", response);
    }
}
