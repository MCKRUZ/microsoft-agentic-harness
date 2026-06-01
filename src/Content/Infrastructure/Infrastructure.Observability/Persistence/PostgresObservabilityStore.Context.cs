using System.Text.Json;
using Domain.AI.Context;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// Foresight context-snapshot persistence. Schema lives in
/// <c>Dashboards/init-db/02-context-snapshots.sql</c>.
/// </summary>
public sealed partial class PostgresObservabilityStore
{
    /// <summary>
    /// camelCase wire shape for <c>loaded_json</c> matching the SignalR / REST
    /// payload the dashboard consumes. Property names are pinned by
    /// <c>SignalRContextSnapshotNotifierTests</c> — do not rename without
    /// updating the client-side type.
    /// </summary>
    private sealed record LoadedItemWire(
        string what,
        int tokens,
        string cat,
        string? @ref);

    private static readonly JsonSerializerOptions s_loadedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task RecordContextSnapshotAsync(
        ContextSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        const string sql = """
            INSERT INTO context_snapshots (
                conversation_id, turn_index, turn_id,
                cat_system, cat_agents, cat_skills, cat_tools, cat_mcp, cat_messages,
                loaded_json, captured_at
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11)
            ON CONFLICT (conversation_id, turn_index) DO UPDATE SET
                turn_id      = EXCLUDED.turn_id,
                cat_system   = EXCLUDED.cat_system,
                cat_agents   = EXCLUDED.cat_agents,
                cat_skills   = EXCLUDED.cat_skills,
                cat_tools    = EXCLUDED.cat_tools,
                cat_mcp      = EXCLUDED.cat_mcp,
                cat_messages = EXCLUDED.cat_messages,
                loaded_json  = EXCLUDED.loaded_json,
                captured_at  = EXCLUDED.captured_at
            """;

        try
        {
            var loadedWire = snapshot.Loaded
                .Select(li => new LoadedItemWire(
                    li.What,
                    li.Tokens,
                    li.Category.ToWire(),
                    li.Reference))
                .ToArray();
            var loadedJson = JsonSerializer.Serialize(loadedWire, s_loadedJson);

            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(snapshot.ConversationId);
            cmd.Parameters.AddWithValue(snapshot.TurnIndex);
            cmd.Parameters.AddWithValue(snapshot.TurnId);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.System);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.Agents);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.Skills);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.Tools);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.Mcp);
            cmd.Parameters.AddWithValue(snapshot.CtxAfter.Messages);
            cmd.Parameters.AddWithValue(loadedJson);
            cmd.Parameters.AddWithValue(snapshot.CapturedAtUtc);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record context snapshot for {ConversationId} turn {TurnIndex}",
                snapshot.ConversationId, snapshot.TurnIndex);
        }
    }

    /// <inheritdoc />
    public async Task<ContextSnapshot?> GetLatestSnapshotAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        const string sql = """
            SELECT conversation_id, turn_index, turn_id,
                   cat_system, cat_agents, cat_skills, cat_tools, cat_mcp, cat_messages,
                   loaded_json, captured_at
            FROM context_snapshots
            WHERE conversation_id = $1
            ORDER BY turn_index DESC
            LIMIT 1
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(conversationId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadSnapshot(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read latest snapshot for {ConversationId}", conversationId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextSnapshot>> GetSnapshotsAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        const string sql = """
            SELECT conversation_id, turn_index, turn_id,
                   cat_system, cat_agents, cat_skills, cat_tools, cat_mcp, cat_messages,
                   loaded_json, captured_at
            FROM context_snapshots
            WHERE conversation_id = $1
            ORDER BY turn_index ASC
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(conversationId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<ContextSnapshot>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadSnapshot(reader));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read snapshots for {ConversationId}", conversationId);
            return Array.Empty<ContextSnapshot>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, CategoryBreakdown>> GetLatestBreakdownsAsync(
        IEnumerable<string> conversationIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        var ids = conversationIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, CategoryBreakdown>();

        // DISTINCT ON picks the row with the highest turn_index per conversation_id
        // in a single scan — no N+1, no correlated subquery.
        const string sql = """
            SELECT DISTINCT ON (conversation_id)
                   conversation_id,
                   cat_system, cat_agents, cat_skills, cat_tools, cat_mcp, cat_messages
            FROM context_snapshots
            WHERE conversation_id = ANY ($1)
            ORDER BY conversation_id, turn_index DESC
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                Value = ids,
            });

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new Dictionary<string, CategoryBreakdown>(ids.Length);
            while (await reader.ReadAsync(cancellationToken))
            {
                var convId = reader.GetString(0);
                results[convId] = new CategoryBreakdown(
                    System: reader.GetInt32(1),
                    Agents: reader.GetInt32(2),
                    Skills: reader.GetInt32(3),
                    Tools: reader.GetInt32(4),
                    Mcp: reader.GetInt32(5),
                    Messages: reader.GetInt32(6));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to batched-read latest breakdowns for {Count} conversations", ids.Length);
            return new Dictionary<string, CategoryBreakdown>();
        }
    }

    private static ContextSnapshot ReadSnapshot(System.Data.Common.DbDataReader reader)
    {
        var conversationId = reader.GetString(0);
        var turnIndex = reader.GetInt32(1);
        var turnId = reader.GetString(2);
        var ctxAfter = new CategoryBreakdown(
            System: reader.GetInt32(3),
            Agents: reader.GetInt32(4),
            Skills: reader.GetInt32(5),
            Tools: reader.GetInt32(6),
            Mcp: reader.GetInt32(7),
            Messages: reader.GetInt32(8));
        var loadedJson = reader.GetString(9);
        var capturedAt = reader.GetFieldValue<DateTimeOffset>(10);

        var loadedWire = JsonSerializer.Deserialize<LoadedItemWire[]>(loadedJson, s_loadedJson)
                         ?? Array.Empty<LoadedItemWire>();
        var loaded = loadedWire
            .Select(w => new LoadedItem(w.what, w.tokens, ContextCategoryWireExtensions.FromWire(w.cat), w.@ref))
            .ToList();

        return new ContextSnapshot(
            conversationId, turnIndex, turnId, ctxAfter, loaded, capturedAt);
    }
}
