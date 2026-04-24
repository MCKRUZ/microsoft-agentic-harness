using System.Text.Json.Serialization;

namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Top-level envelope returned by the Prometheus HTTP API for both
/// <c>/api/v1/query</c> and <c>/api/v1/query_range</c> endpoints.
/// </summary>
public sealed record PrometheusApiResponse
{
    /// <summary>Response status — <c>"success"</c> or <c>"error"</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>The query result payload.</summary>
    [JsonPropertyName("data")]
    public PrometheusData? Data { get; init; }

    /// <summary>Error type when <see cref="Status"/> is <c>"error"</c>.</summary>
    [JsonPropertyName("errorType")]
    public string? ErrorType { get; init; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// The <c>data</c> block inside a Prometheus API response.
/// </summary>
public sealed record PrometheusData
{
    /// <summary>Result type: <c>"vector"</c>, <c>"matrix"</c>, <c>"scalar"</c>, or <c>"string"</c>.</summary>
    [JsonPropertyName("resultType")]
    public string ResultType { get; init; } = string.Empty;

    /// <summary>Array of metric series results.</summary>
    [JsonPropertyName("result")]
    public IReadOnlyList<PrometheusSeriesResult> Result { get; init; } = [];
}

/// <summary>
/// A single series result from a Prometheus query, containing its label set and data points.
/// </summary>
public sealed record PrometheusSeriesResult
{
    /// <summary>Label key-value pairs identifying this series (e.g. <c>__name__</c>, <c>agent_name</c>).</summary>
    [JsonPropertyName("metric")]
    public IReadOnlyDictionary<string, string> Metric { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Single data point for instant queries. Each element is a two-item array: <c>[timestamp, value]</c>.
    /// </summary>
    [JsonPropertyName("value")]
    public IReadOnlyList<object>? Value { get; init; }

    /// <summary>
    /// Array of data points for range queries. Each element is a two-item array: <c>[timestamp, value]</c>.
    /// </summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<IReadOnlyList<object>>? Values { get; init; }
}

/// <summary>
/// Normalized metric series returned to dashboard consumers, abstracting away
/// the Prometheus-specific <c>value</c> vs <c>values</c> distinction.
/// </summary>
public sealed record MetricSeries
{
    /// <summary>Label key-value pairs identifying this series.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Ordered data points in this series.</summary>
    public IReadOnlyList<MetricDataPoint> DataPoints { get; init; } = [];
}

/// <summary>
/// A single timestamped metric value.
/// </summary>
public sealed record MetricDataPoint
{
    /// <summary>Unix timestamp (seconds since epoch, UTC).</summary>
    public double Timestamp { get; init; }

    /// <summary>Metric value as a string (Prometheus convention — may be <c>"NaN"</c>, <c>"+Inf"</c>).</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Normalized query response returned to dashboard consumers.
/// </summary>
public sealed record MetricsQueryResponse
{
    /// <summary>Whether the query succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Result type: <c>"vector"</c>, <c>"matrix"</c>, <c>"scalar"</c>, or <c>"string"</c>.</summary>
    public string ResultType { get; init; } = string.Empty;

    /// <summary>The metric series results.</summary>
    public IReadOnlyList<MetricSeries> Series { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is <c>false</c>.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A curated panel definition that pairs a PromQL query with display metadata,
/// enabling the dashboard to render panels declaratively.
/// </summary>
public sealed record MetricCatalogEntry
{
    /// <summary>Unique identifier for this panel (e.g. <c>"tokens_per_minute"</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable panel title.</summary>
    public required string Title { get; init; }

    /// <summary>Brief description of what this panel shows.</summary>
    public required string Description { get; init; }

    /// <summary>PromQL query to execute (may contain <c>$__range</c> placeholder).</summary>
    public required string Query { get; init; }

    /// <summary>Recommended chart type: <c>"line"</c>, <c>"area"</c>, <c>"bar"</c>, <c>"gauge"</c>, <c>"stat"</c>.</summary>
    public string ChartType { get; init; } = "line";

    /// <summary>Unit for display formatting (e.g. <c>"tokens"</c>, <c>"usd"</c>, <c>"ms"</c>, <c>"percent"</c>).</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>Dashboard route this panel belongs to (e.g. <c>"overview"</c>, <c>"tokens"</c>, <c>"cost"</c>).</summary>
    public required string Category { get; init; }

    /// <summary>Suggested refresh interval in seconds. <c>0</c> means use the global default.</summary>
    public int RefreshIntervalSeconds { get; init; }
}

/// <summary>
/// Health check result for the Prometheus backend.
/// </summary>
public sealed record PrometheusHealthResponse
{
    /// <summary>Whether Prometheus is reachable and responding.</summary>
    public bool Healthy { get; init; }

    /// <summary>Prometheus server version, if reachable.</summary>
    public string? Version { get; init; }

    /// <summary>Error message when <see cref="Healthy"/> is <c>false</c>.</summary>
    public string? Error { get; init; }
}
