namespace Infrastructure.Observability.Persistence;

public sealed partial class PostgresObservabilityStore
{
    /// <summary>The most rows one age-based statement removes, so the first sweep over a large backlog runs in slices.</summary>
    private const int AgedRowBatch = 10_000;

    /// <summary>
    /// Sessions with no activity since the cutoff, oldest first. Activity is the session's start or end, or any row
    /// recorded under it; the start alone is not enough, because a resumed session keeps its original start time. Rows
    /// are locked so a concurrent write waits for the delete rather than racing it.
    /// </summary>
    private const string SelectInactiveSessions = """
        SELECT s.id FROM sessions s
        WHERE s.started_at < $1
          AND COALESCE(s.ended_at, s.started_at) < $1
          AND NOT EXISTS (SELECT 1 FROM session_messages m WHERE m.session_id = s.id AND m.created_at >= $1)
          AND NOT EXISTS (SELECT 1 FROM tool_executions t WHERE t.session_id = s.id AND t.created_at >= $1)
          AND NOT EXISTS (SELECT 1 FROM safety_events e WHERE e.session_id = s.id AND e.created_at >= $1)
          AND NOT EXISTS (SELECT 1 FROM audit_log a WHERE a.session_id = s.id AND a.created_at >= $1)
        ORDER BY s.started_at
        LIMIT $2
        FOR UPDATE SKIP LOCKED
        """;

    /// <summary>
    /// A session's rows, audit first for the same reason as erasure: deleting the session only nulls their link. Its
    /// messages, tool executions and safety events go with it through <c>ON DELETE CASCADE</c>.
    /// </summary>
    private static readonly string[] DeleteSessionStatements =
    [
        "DELETE FROM audit_log WHERE session_id = ANY($1)",
        "DELETE FROM sessions WHERE id = ANY($1)",
    ];

    /// <summary>Rows that belong to no session, aged by their own time, one slice at a time.</summary>
    private static readonly string[] DeleteAgedRowStatements =
    [
        $"DELETE FROM context_snapshot_loaded_bodies WHERE ctid IN (SELECT ctid FROM context_snapshot_loaded_bodies WHERE captured_at < $1 LIMIT {AgedRowBatch})",
        $"DELETE FROM context_snapshots WHERE ctid IN (SELECT ctid FROM context_snapshots WHERE captured_at < $1 LIMIT {AgedRowBatch})",
        $"DELETE FROM audit_log WHERE ctid IN (SELECT ctid FROM audit_log WHERE session_id IS NULL AND created_at < $1 LIMIT {AgedRowBatch})",
    ];

    /// <inheritdoc />
    /// <remarks>
    /// Unlike the write path, failures are not swallowed: the sweep logs them and tries again on its next interval. The
    /// sessions go in one transaction. Context snapshots go by their own capture time, so a conversation that stays
    /// active for longer than the period loses its oldest snapshots while its session is kept.
    /// </remarks>
    public async Task<int> DeleteInactiveSinceAsync(
        DateTimeOffset cutoff, int maxSessions, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);

        var removed = await DeleteInactiveSessionsAsync(cutoff, maxSessions, cancellationToken);
        await DeleteAgedRowsAsync(cutoff, cancellationToken);
        return removed;
    }

    private async Task<int> DeleteInactiveSessionsAsync(
        DateTimeOffset cutoff, int maxSessions, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ids = new List<Guid>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = SelectInactiveSessions;
            select.Transaction = transaction;
            select.Parameters.AddWithValue(cutoff);
            select.Parameters.AddWithValue(maxSessions);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }

        if (ids.Count == 0)
            return 0;

        var sessionIds = ids.ToArray();
        foreach (var sql in DeleteSessionStatements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue(sessionIds);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ids.Count;
    }

    private async Task DeleteAgedRowsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        foreach (var sql in DeleteAgedRowStatements)
        {
            int deleted;
            do
            {
                await using var cmd = _dataSource.CreateCommand(sql);
                cmd.Parameters.AddWithValue(cutoff);
                deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            while (deleted == AgedRowBatch);
        }
    }
}
