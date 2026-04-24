using System.Text.Json;
using Application.AI.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// Persists observability data to PostgreSQL using Npgsql.
/// Designed for append-heavy workloads with fire-and-forget semantics
/// for non-critical writes (audit, safety) to avoid blocking agent turns.
/// </summary>
public sealed class PostgresObservabilityStore : IObservabilityStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresObservabilityStore> _logger;

    public PostgresObservabilityStore(
        string connectionString,
        ILogger<PostgresObservabilityStore> logger)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _logger = logger;
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    public async Task<Guid> StartSessionAsync(
        string conversationId, string agentName, string? model,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO sessions (conversation_id, agent_name, model)
            VALUES ($1, $2, $3)
            ON CONFLICT (conversation_id) DO UPDATE SET started_at = NOW()
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

    public async Task EndSessionAsync(
        Guid sessionId, string status, string? errorMessage,
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
            sessionId, status, errorMessage ?? (object)DBNull.Value);
    }

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
                model = COALESCE($11, model)
            WHERE id = $1
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId, turnCount, toolCallCount, subagentCount,
            totalInputTokens, totalOutputTokens, totalCacheRead,
            totalCacheWrite, totalCostUsd, cacheHitRate,
            (object?)model ?? DBNull.Value);
    }

    // ── Messages ─────────────────────────────────────────────────────────

    public async Task<Guid> RecordMessageAsync(
        Guid sessionId, int turnIndex, string role, string? source,
        string? contentPreview, string? model, int inputTokens, int outputTokens,
        int cacheRead, int cacheWrite, decimal costUsd, decimal cacheHitPct,
        string[]? toolNames = null, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return Guid.Empty;

        const string sql = """
            INSERT INTO session_messages
                (session_id, turn_index, role, source, content_preview, model,
                 input_tokens, output_tokens, cache_read, cache_write,
                 cost_usd, cache_hit_pct, tool_names)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
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

    // ── Tool Executions ──────────────────────────────────────────────────

    public async Task RecordToolExecutionAsync(
        Guid sessionId, Guid? messageId, string toolName, string toolSource,
        int durationMs, string status, string? errorType,
        int? resultSize, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return;

        const string sql = """
            INSERT INTO tool_executions
                (session_id, message_id, tool_name, tool_source,
                 duration_ms, status, error_type, result_size)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """;

        await ExecuteNonQuerySafe(sql, cancellationToken,
            sessionId,
            messageId.HasValue && messageId.Value != Guid.Empty ? messageId.Value : DBNull.Value,
            toolName, toolSource, durationMs, status,
            errorType ?? (object)DBNull.Value,
            resultSize.HasValue ? resultSize.Value : DBNull.Value);
    }

    // ── Safety Events ────────────────────────────────────────────────────

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

    // ── Audit ────────────────────────────────────────────────────────────

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

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task ExecuteNonQuerySafe(
        string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            for (var i = 0; i < parameters.Length; i++)
                cmd.Parameters.AddWithValue(parameters[i]);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Observability store write failed: {Sql}", sql[..Math.Min(sql.Length, 80)]);
        }
    }

    public void Dispose() => _dataSource.Dispose();
}
