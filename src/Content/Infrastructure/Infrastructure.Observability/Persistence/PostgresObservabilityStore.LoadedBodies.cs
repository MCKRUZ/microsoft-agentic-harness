using Domain.AI.Context;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// Foresight loaded-item body persistence — sidecar to <c>context_snapshots</c>.
/// Schema lives in <c>Migrations/003_loaded_bodies.sql</c>, embedded in this assembly and applied
/// by the migration runner.
/// </summary>
public sealed partial class PostgresObservabilityStore
{
    /// <inheritdoc />
    public async Task RecordLoadedBodiesAsync(
        string conversationId,
        int turnIndex,
        IReadOnlyList<LoadedItemBody> bodies,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(bodies);

        if (bodies.Count == 0) return;

        // ON CONFLICT replays the parent context_snapshots' idempotency contract:
        // re-emitting bodies for an existing (conversation_id, turn_index,
        // loaded_index) overwrites rather than duplicates. Done as a single
        // multi-row INSERT so a turn with 8 registration items is one round-trip,
        // not eight.
        const string sql = """
            INSERT INTO context_snapshot_loaded_bodies
                (conversation_id, turn_index, loaded_index, body)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (conversation_id, turn_index, loaded_index) DO UPDATE SET
                body        = EXCLUDED.body,
                captured_at = NOW()
            """;

        try
        {
            foreach (var entry in bodies)
            {
                if (string.IsNullOrEmpty(entry.Body)) continue;

                await using var cmd = _dataSource.CreateCommand(sql);
                cmd.Parameters.AddWithValue(conversationId);
                cmd.Parameters.AddWithValue(turnIndex);
                cmd.Parameters.AddWithValue(entry.LoadedIndex);
                cmd.Parameters.AddWithValue(entry.Body);

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record loaded bodies for {ConversationId} turn {TurnIndex} ({Count} bodies)",
                conversationId, turnIndex, bodies.Count);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLoadedBodyAsync(
        string conversationId,
        int turnIndex,
        int loadedIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        const string sql = """
            SELECT body
            FROM context_snapshot_loaded_bodies
            WHERE conversation_id = $1
              AND turn_index      = $2
              AND loaded_index    = $3
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(conversationId);
            cmd.Parameters.AddWithValue(turnIndex);
            cmd.Parameters.AddWithValue(loadedIndex);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read loaded body for {ConversationId} turn {TurnIndex} idx {LoadedIndex}",
                conversationId, turnIndex, loadedIndex);
            return null;
        }
    }
}
