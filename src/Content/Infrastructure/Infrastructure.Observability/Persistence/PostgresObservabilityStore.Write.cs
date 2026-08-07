using System.Text.Json;
using Domain.AI.Observability.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Observability.Persistence;

public sealed partial class PostgresObservabilityStore
{
    /// <inheritdoc />
    public async Task<Guid> StartSessionAsync(
        string conversationId, string agentName, string? model,
        CancellationToken cancellationToken = default)
    {
        // The conflict branch is reached when a conversation that already has a row opens a session
        // again — the record that would have been adopted lost its session id, or was rebuilt. Its
        // start is being restamped, so the row is describing a session that is beginning; leaving the
        // end markers in place would have it beginning and already over at the same time.
        //
        // The totals go back to zero with it, and must. Reaching here means the recorder had nothing to
        // adopt, so its accumulator restarts from zero and the next UpdateSessionMetricsAsync — which
        // SETs absolute values, not deltas — would overwrite them from zero anyway. Leaving them would
        // publish a row claiming a brand-new session had already spent the previous one's tokens, for
        // as long as it took the first turn to land, and for good if it never did.
        const string sql = """
            INSERT INTO sessions (conversation_id, agent_name, model)
            VALUES ($1, $2, $3)
            ON CONFLICT (conversation_id) DO UPDATE
                SET started_at = NOW(),
                    ended_at = NULL,
                    duration_ms = NULL,
                    status = 'active',
                    error_message = NULL,
                    turn_count = 0,
                    tool_call_count = 0,
                    subagent_count = 0,
                    total_input_tokens = 0,
                    total_output_tokens = 0,
                    total_cache_read = 0,
                    total_cache_write = 0,
                    total_cost_usd = 0,
                    cache_hit_rate = 0
            RETURNING id
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(conversationId);
            cmd.Parameters.AddWithValue(agentName);
            cmd.Parameters.AddWithValue(model ?? (object)DBNull.Value);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is Guid id ? id : Guid.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start session {ConversationId}", conversationId);
            return Guid.Empty;
        }
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(
        Guid sessionId, SessionStatus status, string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        const string sql = """
            UPDATE sessions
            SET ended_at = NOW(),
                duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at))::INT * 1000,
                status = $2,
                error_message = $3
            WHERE id = $1
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId, status.ToDbValue(), errorMessage ?? (object)DBNull.Value);
    }

    /// <inheritdoc />
    public async Task ResumeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        // The WHERE clause is what makes this idempotent, and it is why no read is needed first: on a
        // session that is already open the statement matches nothing and the row — including its start
        // time, which must never be restamped — is untouched. Called on every turn that adopts an
        // existing session, so the common case has to cost one indexed no-op, not a round trip and a
        // conditional write.
        // duration_ms is recomputed rather than cleared. It means "how long so far" on a live session —
        // that is what UpdateSessionMetricsAsync writes on every turn — so blanking it would leave the
        // row with no duration at all until the next turn lands, and permanently if that write fails.
        //
        // error_message is cleared, including when the session had ended with an error. The row states
        // what the conversation is now, and "active, and by the way it failed" is not a state: the
        // dashboard's status badge reads this row, and a live conversation carrying a stale failure
        // reads as broken. The failure itself is not lost — it stays in the turn's own message row and
        // in the audit log, which is where a history of what went wrong belongs.
        const string sql = """
            UPDATE sessions
            SET ended_at = NULL,
                duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at))::INT * 1000,
                status = 'active',
                error_message = NULL
            WHERE id = $1 AND ended_at IS NOT NULL
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken, sessionId);
    }

    /// <inheritdoc />
    public async Task UpdateSessionMetricsAsync(
        Guid sessionId, int turnCount, int toolCallCount, int subagentCount,
        int totalInputTokens, int totalOutputTokens, int totalCacheRead,
        int totalCacheWrite, decimal totalCostUsd, decimal cacheHitRate,
        string? model = null, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        const string sql = """
            UPDATE sessions
            SET turn_count = $2,
                tool_call_count = $3,
                subagent_count = $4,
                total_input_tokens = $5,
                total_output_tokens = $6,
                total_cache_read = $7,
                total_cache_write = $8,
                total_cost_usd = $9,
                cache_hit_rate = $10,
                model = COALESCE($11, model),
                duration_ms = EXTRACT(EPOCH FROM (NOW() - started_at))::INT * 1000
            WHERE id = $1
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId, turnCount, toolCallCount, subagentCount,
            totalInputTokens, totalOutputTokens, totalCacheRead,
            totalCacheWrite, totalCostUsd, cacheHitRate,
            (object?)model ?? DBNull.Value);
    }

    /// <inheritdoc />
    public async Task<Guid> RecordMessageAsync(
        Guid sessionId, int turnIndex, string role, string? source,
        string? contentPreview, string? model, int inputTokens, int outputTokens,
        int cacheRead, int cacheWrite, decimal costUsd, decimal cacheHitPct,
        string[]? toolNames = null, string? contentFull = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return Guid.Empty;

        const string sql = """
            INSERT INTO session_messages
                (session_id, turn_index, role, source, content_preview, content_full, model,
                 input_tokens, output_tokens, cache_read, cache_write,
                 cost_usd, cache_hit_pct, tool_names)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            RETURNING id
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(turnIndex);
            cmd.Parameters.AddWithValue(role);
            cmd.Parameters.AddWithValue(source ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(contentPreview ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(contentFull ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(model ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(inputTokens);
            cmd.Parameters.AddWithValue(outputTokens);
            cmd.Parameters.AddWithValue(cacheRead);
            cmd.Parameters.AddWithValue(cacheWrite);
            cmd.Parameters.AddWithValue(costUsd);
            cmd.Parameters.AddWithValue(cacheHitPct);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = toolNames ?? (object)DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
            });

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is Guid id ? id : Guid.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record message for session {SessionId}", sessionId);
            return Guid.Empty;
        }
    }

    /// <inheritdoc />
    public async Task RecordToolExecutionAsync(
        Guid sessionId, Guid? messageId, string toolName, string toolSource,
        int durationMs, string status, string? errorType = null,
        int? resultSize = null, string? callId = null, string? args = null, string? stdout = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        const string sql = """
            INSERT INTO tool_executions
                (session_id, message_id, tool_name, tool_source,
                 duration_ms, status, error_type, result_size,
                 call_id, args, stdout)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId,
            messageId.HasValue && messageId.Value != Guid.Empty ? messageId.Value : DBNull.Value,
            toolName, toolSource, durationMs, status,
            errorType ?? (object)DBNull.Value,
            resultSize.HasValue ? resultSize.Value : DBNull.Value,
            callId ?? (object)DBNull.Value,
            args ?? (object)DBNull.Value,
            stdout ?? (object)DBNull.Value);
    }

    /// <inheritdoc />
    public async Task RecordSafetyEventAsync(
        Guid sessionId, string phase, string outcome, string? category,
        int? severity, string? filterName,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        const string sql = """
            INSERT INTO safety_events
                (session_id, phase, outcome, category, severity, filter_name)
            VALUES ($1, $2, $3, $4, $5, $6)
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId, phase, outcome,
            category ?? (object)DBNull.Value,
            severity.HasValue ? severity.Value : DBNull.Value,
            filterName ?? (object)DBNull.Value);
    }

    /// <inheritdoc />
    public async Task RecordAuditAsync(
        string operation, string source, Guid? sessionId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO audit_log (operation, source, session_id, metadata)
            VALUES ($1, $2, $3, $4::jsonb)
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            operation, source,
            sessionId.HasValue && sessionId.Value != Guid.Empty ? sessionId.Value : DBNull.Value,
            metadata is not null ? JsonSerializer.Serialize(metadata) : DBNull.Value);
    }
}
