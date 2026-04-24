using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Proxies Prometheus HTTP API queries through AgentHub so the telemetry dashboard
/// talks to a single backend with unified auth and CORS. Consumers never need direct
/// access to Prometheus — this controller handles query construction and result normalization.
/// </summary>
[ApiController]
[Route("api/metrics")]
[Authorize]
public sealed class MetricsController : ControllerBase
{
    private readonly IPrometheusQueryService _prometheus;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public MetricsController(IPrometheusQueryService prometheus) =>
        _prometheus = prometheus;

    /// <summary>
    /// Executes a PromQL instant query against the configured Prometheus server.
    /// Returns the current value of the queried metric(s).
    /// </summary>
    /// <param name="query">PromQL expression (e.g. <c>agentic_harness_agent_tokens_input_total</c>).</param>
    /// <param name="time">Optional evaluation timestamp (RFC3339 or Unix). Defaults to Prometheus server time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized metric series with a single data point per series.</returns>
    [HttpGet("instant")]
    public async Task<ActionResult<MetricsQueryResponse>> QueryInstant(
        [FromQuery] string query,
        [FromQuery] string? time = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "The 'query' parameter is required." });

        if (!IsValidPromQl(query))
            return BadRequest(new { error = "The query contains disallowed characters." });

        var result = await _prometheus.QueryInstantAsync(query, time, cancellationToken);
        return result.Success ? Ok(result) : StatusCode(502, result);
    }

    /// <summary>
    /// Executes a PromQL range query against the configured Prometheus server.
    /// Returns time-series data points at the specified resolution.
    /// </summary>
    /// <param name="query">PromQL expression to evaluate over the range.</param>
    /// <param name="start">Range start — RFC3339 (<c>2024-01-01T00:00:00Z</c>) or Unix timestamp.</param>
    /// <param name="end">Range end — same format as <paramref name="start"/>.</param>
    /// <param name="step">Query resolution step (e.g. <c>15s</c>, <c>1m</c>, <c>5m</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized metric series with data points at each step within the range.</returns>
    [HttpGet("range")]
    public async Task<ActionResult<MetricsQueryResponse>> QueryRange(
        [FromQuery] string query,
        [FromQuery] string start,
        [FromQuery] string end,
        [FromQuery] string step,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "The 'query' parameter is required." });

        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
            return BadRequest(new { error = "The 'start' and 'end' parameters are required." });

        if (string.IsNullOrWhiteSpace(step))
            return BadRequest(new { error = "The 'step' parameter is required." });

        if (!IsValidPromQl(query))
            return BadRequest(new { error = "The query contains disallowed characters." });

        var result = await _prometheus.QueryRangeAsync(query, start, end, step, cancellationToken);
        return result.Success ? Ok(result) : StatusCode(502, result);
    }

    /// <summary>
    /// Returns the curated catalog of panel definitions that the dashboard renders.
    /// Each entry pairs a PromQL query with display metadata (chart type, unit, category).
    /// Template consumers can extend this catalog to add custom panels.
    /// </summary>
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<MetricCatalogEntry>> GetCatalog() =>
        Ok(MetricCatalog.Entries);

    /// <summary>
    /// Checks whether the configured Prometheus server is reachable and healthy.
    /// Returns the Prometheus version when reachable, or an error message when not.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("health")]
    public async Task<ActionResult<PrometheusHealthResponse>> GetHealth(
        CancellationToken cancellationToken = default)
    {
        var result = await _prometheus.GetHealthAsync(cancellationToken);
        return result.Healthy ? Ok(result) : StatusCode(503, result);
    }

    /// <summary>
    /// Basic PromQL injection prevention — blocks shell metacharacters and SQL-style injection.
    /// Prometheus itself validates the query syntax; this is an additional defense-in-depth layer.
    /// </summary>
    private static bool IsValidPromQl(string query)
    {
        var blocked = new[] { ";", "&&", "||", "`", "$(" };
        return !blocked.Any(query.Contains);
    }
}

/// <summary>
/// Curated catalog of PromQL panel definitions for the telemetry dashboard.
/// These map directly to the <c>agent.*</c> metrics emitted by Infrastructure.Observability.
/// Template consumers can extend this list to add custom panels.
/// </summary>
internal static class MetricCatalog
{
    /// <summary>All curated panel definitions, grouped by dashboard category.</summary>
    public static readonly IReadOnlyList<MetricCatalogEntry> Entries =
    [
        // --- Overview ---
        new() { Id = "tokens_per_minute", Title = "Tokens / Minute", Description = "Rate of total token consumption across all agents", Query = "rate(agentic_harness_agent_tokens_total[5m]) * 60", ChartType = "stat", Unit = "tokens/min", Category = "overview" },
        new() { Id = "active_sessions", Title = "Active Sessions", Description = "Number of sessions with activity in the last 5 minutes", Query = "count(agentic_harness_agent_session_active_total > 0) or vector(0)", ChartType = "stat", Unit = "sessions", Category = "overview" },
        new() { Id = "cost_today", Title = "Cost Today", Description = "Estimated LLM cost accrued since midnight UTC", Query = "sum(agentic_harness_agent_tokens_cost_estimated_total) or vector(0)", ChartType = "stat", Unit = "usd", Category = "overview" },
        new() { Id = "cache_hit_rate", Title = "Cache Hit Rate", Description = "Prompt cache hit ratio across all models", Query = "avg(agentic_harness_agent_tokens_cache_hit_rate) or vector(0)", ChartType = "gauge", Unit = "percent", Category = "overview" },
        new() { Id = "safety_violations", Title = "Safety Violations", Description = "Content safety violations in the current window", Query = "sum(agentic_harness_agent_content_safety_violations_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "overview" },
        new() { Id = "budget_status", Title = "Budget Status", Description = "Current budget status (0=clear, 1=warning, 2=critical)", Query = "max(agentic_harness_agent_budget_status) or vector(0)", ChartType = "gauge", Unit = "status", Category = "overview" },

        // --- Tokens ---
        new() { Id = "token_input_rate", Title = "Input Tokens", Description = "Input token consumption rate over time", Query = "rate(agentic_harness_agent_tokens_input_total[5m])", ChartType = "area", Unit = "tokens/s", Category = "tokens" },
        new() { Id = "token_output_rate", Title = "Output Tokens", Description = "Output token consumption rate over time", Query = "rate(agentic_harness_agent_tokens_output_total[5m])", ChartType = "area", Unit = "tokens/s", Category = "tokens" },
        new() { Id = "token_cache_read", Title = "Cache Read Tokens", Description = "Tokens served from prompt cache", Query = "rate(agentic_harness_agent_tokens_cache_read_total[5m])", ChartType = "line", Unit = "tokens/s", Category = "tokens" },
        new() { Id = "token_cache_write", Title = "Cache Write Tokens", Description = "Tokens written to prompt cache", Query = "rate(agentic_harness_agent_tokens_cache_write_total[5m])", ChartType = "line", Unit = "tokens/s", Category = "tokens" },
        new() { Id = "token_by_model", Title = "Tokens by Model", Description = "Total token consumption grouped by model", Query = "sum by (gen_ai_request_model) (agentic_harness_agent_tokens_total)", ChartType = "bar", Unit = "tokens", Category = "tokens" },

        // --- Cost ---
        new() { Id = "cost_rate", Title = "Cost Rate", Description = "Estimated USD cost per minute", Query = "rate(agentic_harness_agent_tokens_cost_estimated_total[5m]) * 60", ChartType = "area", Unit = "usd/min", Category = "cost" },
        new() { Id = "cost_by_model", Title = "Cost by Model", Description = "Cumulative cost breakdown by model", Query = "sum by (gen_ai_request_model) (agentic_harness_agent_tokens_cost_estimated_total)", ChartType = "bar", Unit = "usd", Category = "cost" },
        new() { Id = "cache_savings", Title = "Cache Savings", Description = "Estimated USD saved by prompt caching", Query = "sum(agentic_harness_agent_llm_cache_savings_total) or vector(0)", ChartType = "stat", Unit = "usd", Category = "cost" },
        new() { Id = "budget_spend", Title = "Budget Spend", Description = "Current spend against budget threshold", Query = "sum(agentic_harness_agent_budget_spend_total) or vector(0)", ChartType = "gauge", Unit = "usd", Category = "cost" },

        // --- Tools ---
        new() { Id = "tool_execution_rate", Title = "Tool Executions", Description = "Rate of tool invocations across all agents", Query = "rate(agentic_harness_agent_tool_execution_total[5m]) * 60", ChartType = "line", Unit = "calls/min", Category = "tools" },
        new() { Id = "tool_latency_p95", Title = "Tool Latency (p95)", Description = "95th percentile tool execution latency", Query = "histogram_quantile(0.95, rate(agentic_harness_agent_tool_execution_duration_seconds_bucket[5m]))", ChartType = "line", Unit = "ms", Category = "tools" },
        new() { Id = "tool_error_rate", Title = "Tool Error Rate", Description = "Percentage of tool calls that returned errors", Query = "sum(rate(agentic_harness_agent_tool_execution_errors_total[5m])) / sum(rate(agentic_harness_agent_tool_execution_total[5m])) * 100 or vector(0)", ChartType = "line", Unit = "percent", Category = "tools" },
        new() { Id = "tool_usefulness", Title = "Tool Usefulness", Description = "Average usefulness score across tool invocations", Query = "avg(agentic_harness_agent_tool_usefulness_score) or vector(0)", ChartType = "gauge", Unit = "score", Category = "tools" },

        // --- Safety ---
        new() { Id = "safety_violations_rate", Title = "Violation Rate", Description = "Content safety violations per minute", Query = "rate(agentic_harness_agent_content_safety_violations_total[5m]) * 60", ChartType = "line", Unit = "violations/min", Category = "safety" },
        new() { Id = "safety_by_category", Title = "Violations by Category", Description = "Safety violations grouped by violation category", Query = "sum by (category) (agentic_harness_agent_content_safety_violations_total)", ChartType = "bar", Unit = "count", Category = "safety" },

        // --- Sessions ---
        new() { Id = "session_count", Title = "Session Count", Description = "Total sessions over time", Query = "sum(agentic_harness_agent_session_active_total) or vector(0)", ChartType = "line", Unit = "sessions", Category = "sessions" },
        new() { Id = "session_duration", Title = "Session Duration", Description = "Average session duration", Query = "avg(agentic_harness_agent_session_duration_seconds) or vector(0)", ChartType = "line", Unit = "seconds", Category = "sessions" },

        // --- RAG ---
        new() { Id = "rag_ingestion_rate", Title = "Ingestion Rate", Description = "Document chunks ingested per minute", Query = "rate(agentic_harness_agent_rag_ingestion_chunks_total[5m]) * 60", ChartType = "line", Unit = "chunks/min", Category = "rag" },
        new() { Id = "rag_retrieval_latency", Title = "Retrieval Latency", Description = "RAG retrieval latency percentiles", Query = "histogram_quantile(0.95, rate(agentic_harness_agent_rag_retrieval_duration_seconds_bucket[5m]))", ChartType = "line", Unit = "ms", Category = "rag" },

        // --- Budget ---
        new() { Id = "budget_spend_timeline", Title = "Spend Timeline", Description = "Budget spend accumulation over time", Query = "sum(agentic_harness_agent_budget_spend_total)", ChartType = "area", Unit = "usd", Category = "budget" },
        new() { Id = "budget_threshold_warn", Title = "Warning Threshold", Description = "Budget warning threshold line", Query = "max(agentic_harness_agent_budget_warn_threshold) or vector(0)", ChartType = "line", Unit = "usd", Category = "budget" },
        new() { Id = "budget_threshold_crit", Title = "Critical Threshold", Description = "Budget critical threshold line", Query = "max(agentic_harness_agent_budget_crit_threshold) or vector(0)", ChartType = "line", Unit = "usd", Category = "budget" },
    ];
}
