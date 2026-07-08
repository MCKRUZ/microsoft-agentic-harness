namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The outcome of a batch node deletion, reporting what the store actually removed.
/// Produced by <c>IKnowledgeGraphStore.DeleteNodesAsync</c> so that right-to-erasure
/// receipts (<see cref="ErasureReceipt"/>) can report true counts instead of the
/// requested counts, and so downstream cleanup (feedback-weight purging) knows exactly
/// which edges were cascade-deleted.
/// </summary>
public record NodeDeletionResult
{
    /// <summary>
    /// Number of nodes the store actually removed. Requested IDs that did not exist
    /// are not counted.
    /// </summary>
    public required int NodesDeleted { get; init; }

    /// <summary>
    /// IDs of the edges removed by the cascade (every edge whose source or target was
    /// one of the deleted node IDs). Used to purge edge feedback weights during erasure.
    /// </summary>
    public required IReadOnlyList<string> DeletedEdgeIds { get; init; }

    /// <summary>A result representing no deletions (empty input or nothing matched).</summary>
    public static NodeDeletionResult Empty { get; } = new()
    {
        NodesDeleted = 0,
        DeletedEdgeIds = []
    };
}
