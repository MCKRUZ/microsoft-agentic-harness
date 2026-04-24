namespace Infrastructure.Observability.Tests.Integration;

/// <summary>
/// All 53 Grafana dashboard SQL queries with macros translated to Npgsql parameters.
/// Macro translations:
///   $__timeFilter(col) -> col BETWEEN @from AND @to
///   $__timeGroup(col, '1h') -> date_bin('1 hour', col, TIMESTAMP '2000-01-01')
///   $__variableValue('x') -> @x   /   $x -> @x
///   ('All' IN ($agent_name) OR ...) -> (@agent = 'All' OR agent_name = @agent)
/// </summary>
public static class DashboardQueries
{
    // ═══════════════════════════════════════════════════════════════════
    // sessions.json (6 queries)
    // ═══════════════════════════════════════════════════════════════════

    public const string Sessions_Count = """
        SELECT COUNT(*) AS session_count
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
          AND (@status = 'All' OR status = @status)
        """;

    public const string Sessions_AvgDuration = """
        SELECT COALESCE(AVG(duration_ms), 0) AS avg_duration_ms
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
          AND (@status = 'All' OR status = @status)
          AND duration_ms IS NOT NULL
        """;

    public const string Sessions_AvgCost = """
        SELECT COALESCE(AVG(total_cost_usd), 0) AS avg_cost_usd
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
          AND (@status = 'All' OR status = @status)
        """;

    public const string Sessions_TotalToolCalls = """
        SELECT COALESCE(SUM(tool_call_count), 0) AS total_tool_calls
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
          AND (@status = 'All' OR status = @status)
        """;

    public const string Sessions_List = """
        SELECT conversation_id, started_at, agent_name, model, status, duration_ms,
               turn_count, tool_call_count,
               (total_input_tokens + total_output_tokens) AS total_tokens,
               total_cost_usd, cache_hit_rate
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
          AND (@status = 'All' OR status = @status)
        ORDER BY started_at DESC LIMIT 100
        """;

    public const string Sessions_VarAgentName = """
        SELECT DISTINCT agent_name FROM sessions ORDER BY agent_name
        """;

    // ═══════════════════════════════════════════════════════════════════
    // overview.json (10 queries)
    // ═══════════════════════════════════════════════════════════════════

    public const string Overview_TotalSessions = """
        SELECT COUNT(*)::bigint AS value FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_TotalCost = """
        SELECT COALESCE(SUM(total_cost_usd), 0)::numeric AS value
        FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_TotalTokens = """
        SELECT COALESCE(SUM(total_input_tokens) + SUM(total_output_tokens), 0)::bigint AS value
        FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_AvgCacheHit = """
        SELECT COALESCE(AVG(cache_hit_rate), 0)::numeric AS value
        FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_ErrorRate = """
        SELECT COALESCE(100.0 * SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END)
               / NULLIF(COUNT(*), 0), 0)::numeric AS value
        FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_SessionsOverTime = """
        SELECT date_bin('1 hour', started_at, TIMESTAMP '2000-01-01') AS time,
               COUNT(*) AS sessions
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
        GROUP BY 1 ORDER BY 1
        """;

    public const string Overview_CostOverTime = """
        SELECT day AS time, SUM(total_cost)::numeric AS "Total Cost"
        FROM daily_cost_summary
        WHERE day BETWEEN @from AND @to
        GROUP BY day ORDER BY day
        """;

    public const string Overview_TokenDistribution = """
        SELECT
          COALESCE(SUM(total_input_tokens), 0)::bigint AS input,
          COALESCE(SUM(total_output_tokens), 0)::bigint AS output,
          COALESCE(SUM(total_cache_read), 0)::bigint AS cache_read,
          COALESCE(SUM(total_cache_write), 0)::bigint AS cache_write
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
        """;

    public const string Overview_RecentSessions = """
        SELECT started_at, agent_name, model, duration_ms, turn_count,
               tool_call_count, total_cost_usd, conversation_id
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
        ORDER BY started_at DESC LIMIT 20
        """;

    public const string Overview_CostByModel = """
        SELECT model, SUM(total_cost_usd)::numeric AS cost
        FROM sessions
        WHERE started_at BETWEEN @from AND @to AND model IS NOT NULL
        GROUP BY model ORDER BY cost DESC
        """;

    // ═══════════════════════════════════════════════════════════════════
    // costAnalytics.json (11 queries)
    // ═══════════════════════════════════════════════════════════════════

    public const string Cost_TotalSpend = """
        SELECT COALESCE(SUM(total_cost_usd), 0) AS total_spend
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        """;

    public const string Cost_DailyAverage = """
        SELECT
          CASE
            WHEN GREATEST(EXTRACT(EPOCH FROM (NOW() - MIN(started_at))) / 86400.0, 1) = 0 THEN 0
            ELSE COALESCE(SUM(total_cost_usd), 0)
                 / GREATEST(EXTRACT(EPOCH FROM (NOW() - MIN(started_at))) / 86400.0, 1)
          END AS daily_average
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        """;

    public const string Cost_PerSession = """
        SELECT COALESCE(AVG(total_cost_usd), 0)::numeric AS value
        FROM sessions WHERE started_at BETWEEN @from AND @to
        """;

    public const string Cost_DailyTrend = """
        SELECT day AS time, agent_name AS metric, SUM(total_cost)::float AS value
        FROM daily_cost_summary
        WHERE day BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        GROUP BY day, agent_name ORDER BY day
        """;

    public const string Cost_DailyTokenTrend = """
        SELECT day AS time, SUM(total_input)::float AS input, SUM(total_output)::float AS output
        FROM daily_cost_summary
        WHERE day BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        GROUP BY day ORDER BY day
        """;

    public const string Cost_ByAgent = """
        SELECT agent_name, SUM(total_cost_usd)::numeric AS cost
        FROM sessions
        WHERE started_at BETWEEN @from AND @to AND agent_name IS NOT NULL
        GROUP BY agent_name ORDER BY cost DESC
        """;

    public const string Cost_ByModel = """
        SELECT model, agent_name, SUM(total_cost_usd)::float AS cost
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        GROUP BY model, agent_name ORDER BY model
        """;

    public const string Cost_TokenBreakdown = """
        SELECT
          COALESCE(SUM(total_input_tokens), 0)::bigint AS input,
          COALESCE(SUM(total_output_tokens), 0)::bigint AS output,
          COALESCE(SUM(total_cache_read), 0)::bigint AS cache_read,
          COALESCE(SUM(total_cache_write), 0)::bigint AS cache_write
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        """;

    public const string Cost_TokenUsageOverTime = """
        SELECT day AS time,
               SUM(total_input)::bigint AS "Input Tokens",
               SUM(total_output)::bigint AS "Output Tokens"
        FROM daily_cost_summary
        WHERE day BETWEEN @from AND @to
        GROUP BY day ORDER BY day
        """;

    public const string Cost_TopExpensive = """
        SELECT conversation_id, agent_name, model, total_cost_usd,
               total_input_tokens, total_output_tokens, started_at
        FROM sessions
        WHERE started_at BETWEEN @from AND @to
          AND (@agent = 'All' OR agent_name = @agent)
        ORDER BY total_cost_usd DESC NULLS LAST LIMIT 10
        """;

    public const string Cost_VarAgentName = """
        SELECT DISTINCT agent_name FROM sessions ORDER BY agent_name
        """;

    // ═══════════════════════════════════════════════════════════════════
    // sessionDetail.json (11 queries)
    // ═══════════════════════════════════════════════════════════════════

    public const string Detail_AgentName = """
        SELECT agent_name FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_Model = """
        SELECT model FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_Status = """
        SELECT status FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_Duration = """
        SELECT duration_ms FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_TotalCost = """
        SELECT total_cost_usd FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_TotalTokens = """
        SELECT (total_input_tokens + total_output_tokens) AS total_tokens
        FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_ToolCalls = """
        SELECT tool_call_count FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_CacheHitRate = """
        SELECT cache_hit_rate FROM sessions WHERE conversation_id = @session_id LIMIT 1
        """;

    public const string Detail_MessageTimeline = """
        SELECT m.turn_index, m.role, m.source, m.content_preview, m.model,
               m.input_tokens, m.output_tokens, m.cost_usd,
               array_to_string(m.tool_names, ', ') AS tools, m.created_at
        FROM session_messages m
        JOIN sessions s ON s.id = m.session_id
        WHERE s.conversation_id = @session_id
        ORDER BY m.turn_index, m.created_at
        """;

    public const string Detail_ToolExecutions = """
        SELECT t.tool_name, t.tool_source, t.duration_ms, t.status,
               t.error_type, t.result_size, t.created_at
        FROM tool_executions t
        JOIN sessions s ON s.id = t.session_id
        WHERE s.conversation_id = @session_id
        ORDER BY t.created_at
        """;

    public const string Detail_SafetyEvents = """
        SELECT e.phase, e.outcome, e.category, e.severity, e.filter_name, e.created_at
        FROM safety_events e
        JOIN sessions s ON s.id = e.session_id
        WHERE s.conversation_id = @session_id
        ORDER BY e.created_at
        """;

    public const string Detail_VarSessionId = """
        SELECT conversation_id FROM sessions ORDER BY started_at DESC LIMIT 50
        """;

    // ═══════════════════════════════════════════════════════════════════
    // toolsAndSafety.json (15 queries)
    // ═══════════════════════════════════════════════════════════════════

    public const string Tools_TotalCalls = """
        SELECT COUNT(*) AS value
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
          AND (@tool = 'All' OR tool_name = @tool)
        """;

    public const string Tools_ErrorRate = """
        SELECT COALESCE(100.0 * SUM(CASE WHEN status != 'success' THEN 1 ELSE 0 END)
               / NULLIF(COUNT(*), 0), 0) AS value
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
          AND (@tool = 'All' OR tool_name = @tool)
        """;

    public const string Tools_AvgDuration = """
        SELECT COALESCE(AVG(duration_ms), 0) AS value
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
          AND (@tool = 'All' OR tool_name = @tool)
        """;

    public const string Tools_UniqueTools = """
        SELECT COUNT(DISTINCT tool_name) AS value
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        """;

    public const string Tools_VolumeOverTime = """
        SELECT date_bin('1 hour', created_at, TIMESTAMP '2000-01-01') AS time,
               COUNT(*) AS calls
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        GROUP BY 1 ORDER BY 1
        """;

    public const string Tools_PerformanceTable = """
        SELECT tool_name,
               COUNT(*) AS calls,
               ROUND(AVG(duration_ms)::numeric, 2) AS avg_ms,
               MAX(duration_ms) AS max_ms,
               ROUND(100.0 * SUM(CASE WHEN status != 'success' THEN 1 ELSE 0 END)
                     / NULLIF(COUNT(*), 0), 2) AS error_rate
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        GROUP BY tool_name ORDER BY calls DESC
        """;

    public const string Tools_StatusDistribution = """
        SELECT status, COUNT(*) AS count
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        GROUP BY status ORDER BY count DESC
        """;

    public const string Tools_SourceBreakdown = """
        SELECT tool_source, COUNT(*) AS count
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        GROUP BY tool_source ORDER BY count DESC
        """;

    public const string Tools_RecentErrors = """
        SELECT tool_name, tool_source, error_type, duration_ms, created_at
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to AND status != 'success'
        ORDER BY created_at DESC LIMIT 50
        """;

    public const string Tools_ErrorRateByTool = """
        SELECT tool_name,
               ROUND(100.0 * SUM(CASE WHEN status != 'success' THEN 1 ELSE 0 END)
                     / NULLIF(COUNT(*), 0), 2) AS error_rate
        FROM tool_executions
        WHERE created_at BETWEEN @from AND @to
        GROUP BY tool_name
        HAVING SUM(CASE WHEN status != 'success' THEN 1 ELSE 0 END) > 0
        ORDER BY error_rate DESC
        """;

    public const string Safety_TotalEvents = """
        SELECT COUNT(*) AS value
        FROM safety_events
        WHERE created_at BETWEEN @from AND @to
        """;

    public const string Safety_BlockRate = """
        SELECT COALESCE(100.0 * SUM(CASE WHEN outcome = 'block' THEN 1 ELSE 0 END)
               / NULLIF(COUNT(*), 0), 0) AS value
        FROM safety_events
        WHERE created_at BETWEEN @from AND @to
        """;

    public const string Safety_OutcomeDistribution = """
        SELECT outcome AS metric, COUNT(*) AS value
        FROM safety_events
        WHERE created_at BETWEEN @from AND @to
        GROUP BY outcome ORDER BY outcome
        """;

    public const string Safety_BlocksByCategory = """
        SELECT category, COUNT(*) AS blocks
        FROM safety_events
        WHERE created_at BETWEEN @from AND @to AND outcome = 'block'
        GROUP BY category ORDER BY blocks DESC
        """;

    public const string Safety_RecentBlocks = """
        SELECT phase, outcome, category, severity, filter_name, created_at
        FROM safety_events
        WHERE created_at BETWEEN @from AND @to AND outcome != 'pass'
        ORDER BY created_at DESC LIMIT 50
        """;

    public const string Tools_VarToolName = """
        SELECT DISTINCT tool_name FROM tool_executions ORDER BY tool_name
        """;
}
