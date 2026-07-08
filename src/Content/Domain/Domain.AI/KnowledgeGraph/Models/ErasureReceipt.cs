namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Immutable proof that a right-to-erasure request was fulfilled. All counts report what
/// the underlying delete operations <em>actually removed</em> — as returned by each store —
/// never the requested or estimated counts, so the receipt is trustworthy compliance evidence.
/// </summary>
public record ErasureReceipt
{
    /// <summary>Unique identifier for this erasure request.</summary>
    public required string RequestId { get; init; }
    /// <summary>The scope (user/tenant) whose data was erased.</summary>
    public required string ScopeId { get; init; }
    /// <summary>When the erasure was requested.</summary>
    public required DateTimeOffset RequestedAt { get; init; }
    /// <summary>When the erasure completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
    /// <summary>Number of graph nodes the store actually deleted (missing IDs are not counted).</summary>
    public required int NodesDeleted { get; init; }
    /// <summary>
    /// Number of graph edges actually deleted: the node-cascade edges plus any edges
    /// owned by the erased subject that connected surviving nodes.
    /// </summary>
    public required int EdgesDeleted { get; init; }
    /// <summary>
    /// Number of feedback weight entries actually removed, across both node and edge
    /// weights that referenced erased graph elements.
    /// </summary>
    public required int FeedbackWeightsDeleted { get; init; }
    /// <summary>
    /// Number of vector embeddings deleted. <c>IVectorStore.DeleteAsync</c> returns no
    /// per-item confirmation, so this counts the chunk IDs submitted for deletion —
    /// each call either succeeded or the erasure faulted before producing a receipt.
    /// </summary>
    public required int VectorEmbeddingsDeleted { get; init; }
}
