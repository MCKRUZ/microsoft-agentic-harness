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
        new() { Id = "tokens_per_minute", Title = "Tokens / Minute", Description = "Rate of total token consumption", Query = "rate(agentic_harness_agent_tokens_total_sum[5m]) * 60", ChartType = "stat", Unit = "tokens/min", Category = "overview" },
        new() { Id = "active_sessions", Title = "Active Sessions", Description = "Sessions with recent activity", Query = "agentic_harness_agent_session_active or vector(0)", ChartType = "stat", Unit = "sessions", Category = "overview" },
        new() { Id = "cost_today", Title = "Cost Today", Description = "Estimated LLM cost since midnight UTC", Query = "sum(agentic_harness_agent_tokens_cost_estimated_total) or vector(0)", ChartType = "stat", Unit = "usd", Category = "overview" },
        new() { Id = "cache_hit_rate", Title = "Cache Hit Rate", Description = "Prompt cache hit ratio", Query = "agentic_harness_agent_tokens_cache_hit_rate_sum / agentic_harness_agent_tokens_cache_hit_rate_count or vector(0)", ChartType = "gauge", Unit = "percent", Category = "overview" },
        new() { Id = "safety_violations", Title = "Safety Evaluations", Description = "Content safety evaluations performed", Query = "sum(agentic_harness_agent_safety_evaluations_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "overview" },
        new() { Id = "budget_status", Title = "Budget Status", Description = "Current budget utilization percentage", Query = "max(agentic_harness_agent_budget_utilization_percent) or vector(0)", ChartType = "gauge", Unit = "percent", Category = "overview" },

        // --- Tokens ---
        new() { Id = "tokens_input_total", Title = "Input Tokens", Description = "Total input (prompt) tokens consumed", Query = "sum(agentic_harness_agent_tokens_input_sum) or vector(0)", ChartType = "stat", Unit = "tokens", Category = "tokens" },
        new() { Id = "tokens_output_total", Title = "Output Tokens", Description = "Total output (completion) tokens generated", Query = "sum(agentic_harness_agent_tokens_output_sum) or vector(0)", ChartType = "stat", Unit = "tokens", Category = "tokens" },
        new() { Id = "tokens_cache_read", Title = "Cache Read Tokens", Description = "Tokens served from prompt cache", Query = "sum(agentic_harness_agent_tokens_cache_read_total) or vector(0)", ChartType = "stat", Unit = "tokens", Category = "tokens" },
        new() { Id = "tokens_cache_write", Title = "Cache Write Tokens", Description = "Tokens written to prompt cache", Query = "sum(agentic_harness_agent_tokens_cache_write_total) or vector(0)", ChartType = "stat", Unit = "tokens", Category = "tokens" },
        new() { Id = "tokens_input_rate", Title = "Input Token Rate", Description = "Input tokens per minute over time", Query = "rate(agentic_harness_agent_tokens_input_sum[5m]) * 60", ChartType = "timeseries", Unit = "tokens/min", Category = "tokens" },
        new() { Id = "tokens_output_rate", Title = "Output Token Rate", Description = "Output tokens per minute over time", Query = "rate(agentic_harness_agent_tokens_output_sum[5m]) * 60", ChartType = "timeseries", Unit = "tokens/min", Category = "tokens" },
        new() { Id = "tokens_by_model", Title = "Tokens by Model", Description = "Token distribution across models", Query = "sum by (model) (agentic_harness_agent_tokens_total_sum)", ChartType = "pie", Unit = "tokens", Category = "tokens" },
        new() { Id = "tokens_cache_hit_rate_ts", Title = "Cache Hit Rate Over Time", Description = "Cache hit ratio over time", Query = "agentic_harness_agent_tokens_cache_hit_rate_sum / agentic_harness_agent_tokens_cache_hit_rate_count", ChartType = "timeseries", Unit = "percent", Category = "tokens" },

        // --- Cost ---
        new() { Id = "cost_total", Title = "Total Cost", Description = "Cumulative estimated LLM spend", Query = "sum(agentic_harness_agent_tokens_cost_estimated_total) or vector(0)", ChartType = "stat", Unit = "usd", Category = "cost" },
        new() { Id = "cost_rate", Title = "Cost Rate", Description = "USD burn rate per hour", Query = "rate(agentic_harness_agent_tokens_cost_estimated_total[5m]) * 3600", ChartType = "timeseries", Unit = "usd", Category = "cost" },
        new() { Id = "cost_by_model", Title = "Cost by Model", Description = "Cost breakdown per model", Query = "sum by (model) (agentic_harness_agent_tokens_cost_estimated_total)", ChartType = "pie", Unit = "usd", Category = "cost" },
        new() { Id = "cost_cache_savings", Title = "Cache Savings", Description = "Estimated cost saved via prompt caching", Query = "sum(agentic_harness_agent_tokens_cost_cache_savings_total) or vector(0)", ChartType = "stat", Unit = "usd", Category = "cost" },
        new() { Id = "cost_budget_remaining", Title = "Budget Remaining", Description = "Remaining budget allocation", Query = "max(agentic_harness_agent_budget_remaining) or vector(0)", ChartType = "gauge", Unit = "usd", Category = "cost" },

        // --- Tools ---
        new() { Id = "tools_calls_total", Title = "Tool Calls", Description = "Total tool invocations", Query = "sum(agentic_harness_agent_tool_invocations_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "tools" },
        new() { Id = "tools_errors_total", Title = "Tool Errors", Description = "Total tool execution errors", Query = "sum(agentic_harness_agent_tool_errors_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "tools" },
        new() { Id = "tools_avg_latency", Title = "Avg Latency", Description = "Average tool execution time", Query = "agentic_harness_agent_tool_duration_sum / agentic_harness_agent_tool_duration_count or vector(0)", ChartType = "stat", Unit = "ms", Category = "tools" },
        new() { Id = "tools_result_size", Title = "Avg Result Size", Description = "Average tool result size in characters", Query = "agentic_harness_agent_tool_result_size_sum / agentic_harness_agent_tool_result_size_count or vector(0)", ChartType = "stat", Unit = "chars", Category = "tools" },
        new() { Id = "tools_calls_by_tool", Title = "Calls by Tool", Description = "Invocation count per tool", Query = "sum by (agent_tool_name) (agentic_harness_agent_tool_invocations_total)", ChartType = "bar", Unit = "count", Category = "tools" },
        new() { Id = "tools_latency_by_tool", Title = "Latency by Tool", Description = "Average latency per tool", Query = "sum by (agent_tool_name) (agentic_harness_agent_tool_duration_sum) / sum by (agent_tool_name) (agentic_harness_agent_tool_duration_count)", ChartType = "bar", Unit = "ms", Category = "tools" },
        new() { Id = "tools_error_rate", Title = "Error Rate Over Time", Description = "Tool error rate trend", Query = "rate(agentic_harness_agent_tool_errors_total[5m]) / (rate(agentic_harness_agent_tool_invocations_total[5m]) > 0)", ChartType = "timeseries", Unit = "percent", Category = "tools" },

        // --- Safety ---
        new() { Id = "safety_total", Title = "Safety Evaluations", Description = "Cumulative content safety evaluations", Query = "sum(agentic_harness_agent_safety_evaluations_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "safety" },
        new() { Id = "safety_blocked", Title = "Blocked Requests", Description = "Requests blocked by content safety", Query = "sum(agentic_harness_agent_safety_blocks_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "safety" },
        new() { Id = "safety_violations_ts", Title = "Evaluations Over Time", Description = "Safety evaluation trend", Query = "rate(agentic_harness_agent_safety_evaluations_total[5m]) * 60", ChartType = "timeseries", Unit = "count/min", Category = "safety" },
        new() { Id = "safety_by_category", Title = "Evaluations by Category", Description = "Breakdown by evaluation type", Query = "sum by (category) (agentic_harness_agent_safety_evaluations_total)", ChartType = "pie", Unit = "count", Category = "safety" },
        new() { Id = "safety_block_rate", Title = "Block Rate", Description = "Percentage of requests blocked", Query = "sum(agentic_harness_agent_safety_blocks_total) / (sum(agentic_harness_agent_safety_evaluations_total) > 0)", ChartType = "gauge", Unit = "percent", Category = "safety" },

        // --- Sessions ---
        new() { Id = "sessions_total", Title = "Total Sessions", Description = "Lifetime session count", Query = "sum(agentic_harness_user_activity_sessions_started_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "sessions" },
        new() { Id = "sessions_active", Title = "Active Sessions", Description = "Currently active sessions", Query = "agentic_harness_agent_session_active or vector(0)", ChartType = "stat", Unit = "count", Category = "sessions" },
        new() { Id = "sessions_turns_avg", Title = "Avg Turns/Session", Description = "Average conversation turns per session", Query = "agentic_harness_agent_orchestration_turns_per_conversation_sum / agentic_harness_agent_orchestration_turns_per_conversation_count or vector(0)", ChartType = "stat", Unit = "turns", Category = "sessions" },
        new() { Id = "sessions_duration_avg", Title = "Avg Duration", Description = "Average session duration", Query = "agentic_harness_agent_orchestration_conversation_duration_sum / agentic_harness_agent_orchestration_conversation_duration_count or vector(0)", ChartType = "stat", Unit = "ms", Category = "sessions" },
        new() { Id = "sessions_active_ts", Title = "Active Sessions Over Time", Description = "Session concurrency over time", Query = "agentic_harness_agent_session_active or vector(0)", ChartType = "timeseries", Unit = "count", Category = "sessions" },
        new() { Id = "sessions_turns_ts", Title = "Turns Over Time", Description = "Conversation turns per minute", Query = "rate(agentic_harness_user_activity_turns_total[5m]) * 60", ChartType = "timeseries", Unit = "turns/min", Category = "sessions" },

        // --- RAG ---
        new() { Id = "rag_ingestion_total", Title = "Documents Ingested", Description = "Total documents processed for RAG", Query = "sum(agentic_harness_rag_ingestion_documents_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "rag" },
        new() { Id = "rag_retrieval_total", Title = "Retrievals", Description = "Total RAG retrieval operations", Query = "sum(agentic_harness_rag_retrieval_queries_total) or vector(0)", ChartType = "stat", Unit = "count", Category = "rag" },
        new() { Id = "rag_avg_latency", Title = "Avg Retrieval Latency", Description = "Average RAG retrieval time", Query = "agentic_harness_rag_retrieval_duration_sum / agentic_harness_rag_retrieval_duration_count or vector(0)", ChartType = "stat", Unit = "ms", Category = "rag" },
        new() { Id = "rag_chunks_avg", Title = "Avg Chunks Returned", Description = "Average chunks per retrieval", Query = "agentic_harness_rag_retrieval_chunks_returned_sum / agentic_harness_rag_retrieval_chunks_returned_count or vector(0)", ChartType = "stat", Unit = "chunks", Category = "rag" },
        new() { Id = "rag_ingestion_rate", Title = "Ingestion Rate", Description = "Document ingestion throughput", Query = "rate(agentic_harness_rag_ingestion_documents_total[5m]) * 60", ChartType = "timeseries", Unit = "docs/min", Category = "rag" },
        new() { Id = "rag_retrieval_latency_ts", Title = "Retrieval Latency Over Time", Description = "RAG retrieval latency trend", Query = "agentic_harness_rag_retrieval_duration_sum / agentic_harness_rag_retrieval_duration_count", ChartType = "timeseries", Unit = "ms", Category = "rag" },

        // --- Budget ---
        new() { Id = "budget_spent", Title = "Total Spent", Description = "Total budget consumed", Query = "agentic_harness_agent_budget_current_spend or vector(0)", ChartType = "stat", Unit = "usd", Category = "budget" },
        new() { Id = "budget_limit", Title = "Budget Limit", Description = "Configured budget ceiling", Query = "agentic_harness_agent_budget_threshold_critical or vector(0)", ChartType = "stat", Unit = "usd", Category = "budget" },
        new() { Id = "budget_remaining", Title = "Remaining", Description = "Budget remaining before limit", Query = "agentic_harness_agent_budget_threshold_critical - agentic_harness_agent_budget_current_spend or vector(0)", ChartType = "stat", Unit = "usd", Category = "budget" },
        new() { Id = "budget_utilization", Title = "Utilization", Description = "Budget utilization percentage", Query = "agentic_harness_agent_budget_current_spend / agentic_harness_agent_budget_threshold_critical or vector(0)", ChartType = "gauge", Unit = "percent", Category = "budget" },
        new() { Id = "budget_spend_rate", Title = "Spend Rate", Description = "Budget burn rate over time", Query = "rate(agentic_harness_user_activity_cost_accrued_total[5m]) * 3600", ChartType = "timeseries", Unit = "usd/hr", Category = "budget" },
        new() { Id = "budget_status", Title = "Budget Status", Description = "Budget status (0=clear, 1=warning, 2=critical)", Query = "agentic_harness_agent_budget_status or vector(0)", ChartType = "stat", Unit = "status", Category = "budget" },
    ];
}
