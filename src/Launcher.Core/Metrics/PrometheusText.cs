using System.Globalization;
using System.Text;

namespace Launcher.Core.Metrics;

/// <summary>Which of the two Prometheus metric types a series is. There is no histogram or summary
/// here because nothing the runner exports needs one: every series is either a level read at scrape
/// time or a count the supervisor already keeps.</summary>
public enum MetricType
{
    Gauge,
    Counter,
}

/// <summary>The Prometheus text exposition format, written by hand.
///
/// Hand-rolled rather than taken from prometheus-net because Launcher.Core is BCL-only by rule and the
/// numbers being exported are Core's own (see the note on <see cref="RunnerMetrics"/> for the whole
/// argument). The format is a short, frozen spec, and this is the entirety of the part of it a scrape
/// target has to produce:
///
/// <code>
///   # HELP name help text
///   # TYPE name gauge
///   name{label="value"} 12.5
/// </code>
///
/// The three things that are easy to get wrong, and are therefore the only real content of this file:
/// backslash/newline/quote escaping in label values, the invariant number format, and never emitting a
/// series whose value is unknown.</summary>
public sealed class PrometheusText
{
    private readonly StringBuilder _out = new();
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);

    /// <summary>The content type a scraper expects. Version 0.0.4 is the text format every Prometheus
    /// and every OpenMetrics-capable scraper still accepts.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Declare a metric family. Emitting HELP and TYPE more than once for the same name makes
    /// a scraper reject the whole payload, so this is idempotent rather than the caller's problem: the
    /// per-instance series below are written inside a loop and would otherwise redeclare on every
    /// instance.</summary>
    public PrometheusText Declare(string name, MetricType type, string help)
    {
        if (!_declared.Add(name))
            return this;

        _out.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
        _out.Append("# TYPE ").Append(name).Append(' ')
            .Append(type == MetricType.Counter ? "counter" : "gauge").Append('\n');
        return this;
    }

    public PrometheusText Sample(string name, double value, params (string Key, string Value)[] labels)
    {
        _out.Append(name);

        if (labels.Length > 0)
        {
            _out.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                    _out.Append(',');
                _out.Append(labels[i].Key).Append("=\"").Append(EscapeLabel(labels[i].Value))
                    .Append('"');
            }
            _out.Append('}');
        }

        _out.Append(' ').Append(Format(value)).Append('\n');
        return this;
    }

    /// <summary>A sample whose value may be unknown, in which case nothing is written at all.
    ///
    /// Absent is not zero, and this is the distinction the whole exporter turns on. A supervised
    /// process that has not answered getinfo yet has an unknown player count; writing 0 for it draws a
    /// server that is empty, which is a different and much more alarming picture than a server that has
    /// not reported. The same goes for CPU on an adopted process the OS will not report times for.
    /// Prometheus renders a gap for a missing series, which is the honest shape.</summary>
    public PrometheusText SampleIfKnown(string name, double? value,
        params (string Key, string Value)[] labels) =>
        value is { } known ? Sample(name, known, labels) : this;

    public override string ToString() => _out.ToString();

    /// <summary>Invariant, and never scientific notation for the magnitudes here.
    ///
    /// "R" round-trips, so a CPU percentage does not acquire digits it never had, and the invariant
    /// culture is what stops a runner on a German box emitting "12,5" - which a scraper reads as two
    /// values and rejects the payload over.</summary>
    private static string Format(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "+Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Label values are the only place operator-supplied text reaches the payload: an instance
    /// name, a hostname, a build id. Backslash, quote and newline each terminate a label value early,
    /// so an unescaped one does not corrupt one series, it corrupts the rest of the scrape.</summary>
    internal static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    /// <summary>HELP is terminated by the newline and has no quoting, so only backslash and newline
    /// need handling.</summary>
    internal static string EscapeHelp(string help) =>
        help.Replace("\\", "\\\\").Replace("\n", "\\n");
}
