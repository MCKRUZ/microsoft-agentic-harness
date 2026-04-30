using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Development-only <see cref="IPrometheusQueryService"/> that generates synthetic
/// metric data so template consumers can preview the dashboard without running
/// Prometheus. Registered when <c>AppConfig:Prometheus:EnableDemoData</c> is <c>true</c>.
/// </summary>
public sealed class DemoMetricsService : IPrometheusQueryService
{
    private static readonly Dictionary<string, (double BaseValue, double Variance)> MetricProfiles = new()
    {
        ["tokens_total"] = (170_000, 0.15),
        ["tokens_input"] = (125_000, 0.12),
        ["tokens_output"] = (45_000, 0.18),
        ["tokens_cache_read"] = (80_000, 0.10),
        ["tokens_cache_write"] = (15_000, 0.20),
        ["tokens_cost"] = (0.0523, 0.25),
        ["cost_cache_savings"] = (0.012, 0.15),
        ["cache_hit_rate"] = (0.72, 0.08),
        ["session_active"] = (3, 0.30),
        ["sessions_started"] = (25, 0.15),
        ["turns_per_conversation"] = (8.5, 0.12),
        ["conversation_duration"] = (180_000, 0.20),
        ["safety_evaluations"] = (15, 0.25),
        ["safety_blocks"] = (3, 0.35),
        ["tool_invocations"] = (150, 0.18),
        ["tool_errors"] = (5, 0.40),
        ["tool_duration"] = (250, 0.22),
        ["tool_result_size"] = (2048, 0.15),
        ["rag_ingestion"] = (50, 0.20),
        ["rag_retrieval_queries"] = (200, 0.18),
        ["rag_retrieval_duration"] = (120, 0.25),
        ["rag_retrieval_chunks"] = (4.2, 0.15),
        ["budget_current_spend"] = (12.5, 0.05),
        ["budget_threshold"] = (50.0, 0.0),
        ["budget_remaining"] = (37.5, 0.05),
        ["budget_utilization"] = (0.25, 0.10),
        ["budget_status"] = (0, 0.0),
        ["user_activity_turns"] = (45, 0.20),
        ["user_activity_cost"] = (0.035, 0.25),
    };

    private static readonly Dictionary<string, (string LabelKey, string[] LabelValues, double[] BaseValues)> MultiSeriesProfiles = new()
    {
        ["by (model)"] = ("model", ["claude-3-opus", "claude-3-sonnet", "gpt-4o"], [5000, 12000, 3000]),
        ["by (agent_tool_name)"] = ("agent_tool_name", ["file_search", "code_exec", "web_fetch"], [150, 80, 45]),
        ["by (category)"] = ("category", ["violence", "sexual_content", "hate_speech"], [5, 2, 1]),
        ["by (source)"] = ("source", ["docs.md", "readme.md", "api-ref.md"], [120, 85, 60]),
    };

    /// <inheritdoc />
    public Task<MetricsQueryResponse> QueryInstantAsync(
        string query, string? time = null, CancellationToken cancellationToken = default)
    {
        var ts = time != null && double.TryParse(time, out var t) ? t : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var series = BuildSeries(query, ts, ts, 1);
        var result = series.Select(s => new MetricSeries
        {
            Labels = s.Labels,
            DataPoints = s.DataPoints.TakeLast(1).ToList(),
        }).ToList();

        return Task.FromResult(new MetricsQueryResponse { Success = true, ResultType = "vector", Series = result });
    }

    /// <inheritdoc />
    public Task<MetricsQueryResponse> QueryRangeAsync(
        string query, string start, string end, string step,
        CancellationToken cancellationToken = default)
    {
        var startTs = double.TryParse(start, out var s) ? s : DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var endTs = double.TryParse(end, out var e) ? e : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stepSec = ParseStep(step);

        var series = BuildSeries(query, startTs, endTs, stepSec);
        return Task.FromResult(new MetricsQueryResponse { Success = true, ResultType = "matrix", Series = series });
    }

    /// <inheritdoc />
    public Task<PrometheusHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PrometheusHealthResponse { Healthy = true, Version = "demo-mode" });

    private static IReadOnlyList<MetricSeries> BuildSeries(string query, double startTs, double endTs, double stepSec)
    {
        var multiKey = MultiSeriesProfiles.Keys.FirstOrDefault(k => query.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (multiKey != null)
        {
            var (labelKey, labelValues, baseValues) = MultiSeriesProfiles[multiKey];
            return labelValues.Select((label, idx) => GenerateSeries(
                new Dictionary<string, string> { [labelKey] = label },
                baseValues[idx], 0.15, startTs, endTs, stepSec, query.GetHashCode() + idx
            )).ToList();
        }

        var (baseValue, variance) = ResolveProfile(query);
        var isRate = query.Contains("rate(", StringComparison.OrdinalIgnoreCase);
        if (isRate) baseValue *= 0.01;

        return [GenerateSeries(
            new Dictionary<string, string> { ["__name__"] = ExtractMetricName(query) },
            baseValue, variance, startTs, endTs, stepSec, query.GetHashCode()
        )];
    }

    private static MetricSeries GenerateSeries(
        IReadOnlyDictionary<string, string> labels,
        double baseValue, double variance,
        double startTs, double endTs, double stepSec, int seed)
    {
        var points = new List<MetricDataPoint>();
        var rng = new Random(seed);
        var idx = 0;

        for (var ts = startTs; ts <= endTs; ts += stepSec)
        {
            var wave = Math.Sin(idx / 3.0) * baseValue * variance;
            var noise = (rng.NextDouble() - 0.5) * baseValue * variance * 0.5;
            var value = Math.Max(0, baseValue + wave + noise);

            points.Add(new MetricDataPoint
            {
                Timestamp = ts,
                Value = baseValue >= 1 ? value.ToString("F0") : value.ToString("F4"),
            });
            idx++;
        }

        return new MetricSeries { Labels = labels, DataPoints = points };
    }

    private static (double BaseValue, double Variance) ResolveProfile(string query)
    {
        foreach (var (key, profile) in MetricProfiles)
        {
            if (query.Contains(key, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return (42, 0.15);
    }

    private static string ExtractMetricName(string query)
    {
        var start = query.IndexOf("agentic_harness_", StringComparison.Ordinal);
        if (start < 0) return "metric";
        var end = query.IndexOfAny([')', '[', ' ', '+', '/'], start);
        return end > start ? query[start..end] : query[start..];
    }

    private static double ParseStep(string step)
    {
        if (string.IsNullOrEmpty(step)) return 60;
        var span = step.AsSpan().TrimEnd();
        if (span.EndsWith("s") && double.TryParse(span[..^1], out var sec)) return sec;
        if (span.EndsWith("m") && double.TryParse(span[..^1], out var min)) return min * 60;
        if (span.EndsWith("h") && double.TryParse(span[..^1], out var hr)) return hr * 3600;
        return double.TryParse(step, out var raw) ? raw : 60;
    }
}
