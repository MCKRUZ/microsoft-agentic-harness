using Domain.AI.Observability.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Observability.Persistence;

public sealed partial class PostgresObservabilityStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
        int limit = 50, int offset = 0, SessionStatus? status = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        CancellationToken cancellationToken = default)
    {
        const string columns = """
            SELECT id, conversation_id, agent_name, model, started_at, ended_at, duration_ms,
                   turn_count, tool_call_count, subagent_count, total_input_tokens, total_output_tokens,
                   total_cache_read, total_cache_write, total_cost_usd, cache_hit_rate,
                   status, error_message, created_at
            FROM sessions
            """;

        var clauses = new List<string>();
        var paramIndex = 3;

        if (status.HasValue)
            clauses.Add($"status = ${paramIndex++}");
        if (since.HasValue)
            clauses.Add($"started_at >= ${paramIndex++}");
        if (until.HasValue)
            clauses.Add($"started_at < ${paramIndex++}");

        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        var sql = $"{columns} {where} ORDER BY started_at DESC LIMIT $1 OFFSET $2";

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);
            if (status.HasValue)
                cmd.Parameters.AddWithValue(status.Value.ToDbValue());
            if (since.HasValue)
                cmd.Parameters.AddWithValue(since.Value);
            if (until.HasValue)
                cmd.Parameters.AddWithValue(until.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<SessionRecord>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadSessionRecord(reader));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read sessions (limit={Limit}, offset={Offset})", limit, offset);
            return Array.Empty<SessionRecord>();
        }
    }

    /// <inheritdoc />
    public async Task<SessionRecord?> GetSessionByIdAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, conversation_id, agent_name, model, started_at, ended_at, duration_ms,
                   turn_count, tool_call_count, subagent_count, total_input_tokens, total_output_tokens,
                   total_cache_read, total_cache_write, total_cost_usd, cache_hit_rate,
                   status, error_message, created_at
            FROM sessions
            WHERE id = $1
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadSessionRecord(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read session {SessionId}", sessionId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionMessageRecord>> GetSessionMessagesAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        // List view doesn't need the full body, just the preview.
        const string sql = """
            SELECT id, session_id, turn_index, role, source, content_preview, NULL::text, model,
                   input_tokens, output_tokens, cache_read, cache_write, cost_usd, cache_hit_pct,
                   tool_names, created_at
            FROM session_messages
            WHERE session_id = $1
            ORDER BY turn_index
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<SessionMessageRecord>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadSessionMessageRecord(reader));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read messages for session {SessionId}", sessionId);
            return Array.Empty<SessionMessageRecord>();
        }
    }

    /// <inheritdoc />
    public async Task<SessionMessageRecord?> GetMessageByIdAsync(
        Guid sessionId, Guid messageId, CancellationToken cancellationToken = default)
    {
        // Detail view selects the full body so the file-body endpoint can
        // return content_full to the dashboard.
        const string sql = """
            SELECT id, session_id, turn_index, role, source, content_preview, content_full, model,
                   input_tokens, output_tokens, cache_read, cache_write, cost_usd, cache_hit_pct,
                   tool_names, created_at
            FROM session_messages
            WHERE session_id = $1 AND id = $2
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(messageId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadSessionMessageRecord(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read message {MessageId} for session {SessionId}",
                messageId, sessionId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolExecutionRecord>> GetSessionToolExecutionsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        // List view returns null bodies; the per-invocation route fetches them.
        const string sql = """
            SELECT id, session_id, message_id, tool_name, tool_source, duration_ms, status,
                   error_type, result_size, call_id, NULL::text, NULL::text, created_at
            FROM tool_executions
            WHERE session_id = $1
            ORDER BY created_at
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<ToolExecutionRecord>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadToolExecutionRecord(reader));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tool executions for session {SessionId}", sessionId);
            return Array.Empty<ToolExecutionRecord>();
        }
    }

    /// <inheritdoc />
    public async Task<ToolExecutionRecord?> GetToolExecutionByIdAsync(
        Guid sessionId, Guid invocationId, CancellationToken cancellationToken = default)
    {
        // Detail view selects args + stdout for the per-invocation page.
        const string sql = """
            SELECT id, session_id, message_id, tool_name, tool_source, duration_ms, status,
                   error_type, result_size, call_id, args, stdout, created_at
            FROM tool_executions
            WHERE session_id = $1 AND id = $2
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(invocationId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadToolExecutionRecord(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read tool execution {InvocationId} for session {SessionId}",
                invocationId, sessionId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafetyEventRecord>> GetSessionSafetyEventsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, session_id, phase, outcome, category, severity, filter_name, created_at
            FROM safety_events
            WHERE session_id = $1
            ORDER BY created_at
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<SafetyEventRecord>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadSafetyEventRecord(reader));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read safety events for session {SessionId}", sessionId);
            return Array.Empty<SafetyEventRecord>();
        }
    }

    private static SessionRecord ReadSessionRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        ConversationId = reader.GetString(1),
        AgentName = reader.GetString(2),
        Model = reader.IsDBNull(3) ? null : reader.GetString(3),
        StartedAt = reader.GetFieldValue<DateTimeOffset>(4),
        EndedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        DurationMs = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        TurnCount = reader.GetInt32(7),
        ToolCallCount = reader.GetInt32(8),
        SubagentCount = reader.GetInt32(9),
        TotalInputTokens = reader.GetInt32(10),
        TotalOutputTokens = reader.GetInt32(11),
        TotalCacheRead = reader.GetInt32(12),
        TotalCacheWrite = reader.GetInt32(13),
        TotalCostUsd = reader.GetDecimal(14),
        CacheHitRate = reader.GetDecimal(15),
        Status = reader.GetString(16),
        ErrorMessage = reader.IsDBNull(17) ? null : reader.GetString(17),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(18)
    };

    private static SessionMessageRecord ReadSessionMessageRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        SessionId = reader.GetGuid(1),
        TurnIndex = reader.GetInt32(2),
        Role = reader.GetString(3),
        Source = reader.IsDBNull(4) ? null : reader.GetString(4),
        ContentPreview = reader.IsDBNull(5) ? null : reader.GetString(5),
        ContentFull = reader.IsDBNull(6) ? null : reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        InputTokens = reader.GetInt32(8),
        OutputTokens = reader.GetInt32(9),
        CacheRead = reader.GetInt32(10),
        CacheWrite = reader.GetInt32(11),
        CostUsd = reader.GetDecimal(12),
        CacheHitPct = reader.GetDecimal(13),
        ToolNames = reader.IsDBNull(14) ? null : reader.GetFieldValue<string[]>(14),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(15)
    };

    private static ToolExecutionRecord ReadToolExecutionRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        SessionId = reader.GetGuid(1),
        MessageId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        ToolName = reader.GetString(3),
        ToolSource = reader.IsDBNull(4) ? null : reader.GetString(4),
        DurationMs = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        Status = reader.GetString(6),
        ErrorType = reader.IsDBNull(7) ? null : reader.GetString(7),
        ResultSize = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        CallId = reader.IsDBNull(9) ? null : reader.GetString(9),
        Args = reader.IsDBNull(10) ? null : reader.GetString(10),
        Stdout = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(12)
    };

    private static SafetyEventRecord ReadSafetyEventRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        SessionId = reader.GetGuid(1),
        Phase = reader.GetString(2),
        Outcome = reader.GetString(3),
        Category = reader.IsDBNull(4) ? null : reader.GetString(4),
        Severity = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        FilterName = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7)
    };
}
