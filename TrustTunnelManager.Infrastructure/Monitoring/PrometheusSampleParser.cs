using System.Globalization;
using System.Text.RegularExpressions;

namespace TrustTunnelManager.Infrastructure.Monitoring;

internal sealed record PrometheusSample(
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    long Value);

/// <summary>
/// Parses the integer gauge and counter samples used by TrustTunnel's metrics endpoint.
/// Unrelated Prometheus samples are safely ignored.
/// </summary>
internal static partial class PrometheusSampleParser
{
    private static readonly Regex SamplePattern = new(
        @"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(?<labels>.*)\})?\s+(?<value>[+-]?\d+)(?:\s+\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LabelPattern = new(
        @"(?<name>[a-zA-Z_][a-zA-Z0-9_]*)=""(?<value>(?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PrometheusSample> Parse(string exposition)
    {
        ArgumentNullException.ThrowIfNull(exposition);

        var samples = new List<PrometheusSample>();

        foreach (var line in exposition.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = line.Trim();

            if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#'))
            {
                continue;
            }

            var match = SamplePattern.Match(trimmedLine);

            if (!match.Success ||
                !long.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            var labels = ParseLabels(match.Groups["labels"].Value);

            samples.Add(new PrometheusSample(
                match.Groups["name"].Value,
                labels,
                Math.Max(0, value)));
        }

        return samples;
    }

    public static long Sum(
        IEnumerable<PrometheusSample> samples,
        string metricName)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        var total = 0L;

        foreach (var sample in samples)
        {
            if (!string.Equals(sample.Name, metricName, StringComparison.Ordinal))
            {
                continue;
            }

            total = SaturatingAdd(total, sample.Value);
        }

        return total;
    }

    public static IReadOnlyDictionary<string, long> GetSessionsByProtocol(
        IEnumerable<PrometheusSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            if (!string.Equals(sample.Name, "client_sessions", StringComparison.Ordinal))
            {
                continue;
            }

            var protocol = sample.Labels.TryGetValue("protocol_type", out var value)
                ? value
                : "Unknown";

            result[protocol] = result.TryGetValue(protocol, out var current)
                ? SaturatingAdd(current, sample.Value)
                : sample.Value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseLabels(string labelsText)
    {
        if (string.IsNullOrWhiteSpace(labelsText))
        {
            return new Dictionary<string, string>();
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in LabelPattern.Matches(labelsText))
        {
            labels[match.Groups["name"].Value] = UnescapeLabelValue(
                match.Groups["value"].Value);
        }

        return labels;
    }

    private static string UnescapeLabelValue(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];

            if (current != '\\' || index + 1 >= value.Length)
            {
                result.Append(current);
                continue;
            }

            var next = value[++index];

            result.Append(next switch
            {
                'n' => '\n',
                '"' => '"',
                '\\' => '\\',
                _ => next
            });
        }

        return result.ToString();
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
