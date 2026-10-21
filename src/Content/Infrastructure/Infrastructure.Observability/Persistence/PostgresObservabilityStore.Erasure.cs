using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.Observability.Persistence;

public sealed partial class PostgresObservabilityStore : IPersonalDataEraser
{
    /// <summary>
    /// Everything recorded for a set of conversations, in an order the foreign keys allow. Audit rows go first because
    /// deleting a session only nulls their link and would leave their metadata behind; a session's messages, tool
    /// executions and safety events go with it through <c>ON DELETE CASCADE</c>; context snapshots carry the
    /// conversation id directly.
    /// </summary>
    private static readonly string[] EraseStatements =
    [
        "DELETE FROM audit_log WHERE session_id IN (SELECT id FROM sessions WHERE conversation_id = ANY($1))",
        "DELETE FROM context_snapshot_loaded_bodies WHERE conversation_id = ANY($1)",
        "DELETE FROM context_snapshots WHERE conversation_id = ANY($1)",
        "DELETE FROM sessions WHERE conversation_id = ANY($1)",
    ];

    /// <inheritdoc />
    public string ErasureStoreName => "observability database";

    /// <inheritdoc />
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.NamedConversations;

    /// <inheritdoc />
    /// <remarks>
    /// Rows carry no user, only a conversation id, so the person's rows are those of
    /// <see cref="ErasureSubject.ConversationIds"/>. One transaction, and unlike the write path, failures are not
    /// swallowed: a telemetry write may be dropped quietly, an erasure may not. Returns the total rows removed directly
    /// (cascaded child rows are not counted separately).
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.ConversationIds.Count == 0)
            return 0;

        var ids = subject.ConversationIds.ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var removed = 0;
        foreach (var sql in EraseStatements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue(ids);
            removed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }
}
