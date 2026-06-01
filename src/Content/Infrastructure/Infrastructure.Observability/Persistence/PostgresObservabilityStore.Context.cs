using Domain.AI.Context;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// PR 3 part 2 — context-snapshot persistence (stubbed).
/// </summary>
/// <remarks>
/// These four methods are declared so the implementation type satisfies
/// <see cref="Application.AI.Common.Interfaces.IObservabilityStore"/> after the
/// interface gained snapshot members. The real Postgres SQL + DDL lands in the
/// next PR-3 layer (task #24): a <c>context_snapshots</c> table keyed by
/// <c>(conversation_id, turn_index)</c> with a unique index for idempotent
/// replays, the four methods implemented over raw Npgsql commands following the
/// <see cref="PostgresObservabilityStore.Write"/> pattern, and a batched
/// <c>GetLatestBreakdownsAsync</c> that avoids N+1 for the sessions-list path.
///
/// Until that lands, writes are silent no-ops and reads return empty so the
/// system stays functional while the rest of the PR-3 layers wire up.
/// </remarks>
public sealed partial class PostgresObservabilityStore
{
    /// <inheritdoc />
    public Task RecordContextSnapshotAsync(
        ContextSnapshot snapshot, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<ContextSnapshot?> GetLatestSnapshotAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult<ContextSnapshot?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextSnapshot>> GetSnapshotsAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ContextSnapshot>>(Array.Empty<ContextSnapshot>());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, CategoryBreakdown>> GetLatestBreakdownsAsync(
        IEnumerable<string> conversationIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, CategoryBreakdown>>(
            new Dictionary<string, CategoryBreakdown>());
}
