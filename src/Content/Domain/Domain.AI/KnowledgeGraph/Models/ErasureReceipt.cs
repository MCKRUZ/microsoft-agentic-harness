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
    /// Number of documents whose vector embeddings were submitted for deletion.
    /// <c>IVectorStore.DeleteAsync</c> deletes by <em>document ID</em> and returns no per-item
    /// confirmation, so this counts the distinct document IDs (derived from the erased nodes'
    /// chunk IDs) submitted to the vector store — each call either succeeded or the erasure
    /// faulted before producing a receipt. Zero when no vector store is configured.
    /// </summary>
    public required int VectorEmbeddingsDeleted { get; init; }

    /// <summary>
    /// Number of documents whose BM25/full-text rows were submitted for deletion, by document
    /// ID (derived from the erased nodes' chunk IDs). RAPTOR summary rows share the same
    /// document ID and drop out via the same document-scoped delete. Zero when no BM25 store
    /// is configured. Defaults to zero so pre-existing construction sites remain valid.
    /// </summary>
    public int Bm25DocumentsDeleted { get; init; }

    /// <summary>
    /// Number of cross-session memory records purged for the erased owner, across the store's
    /// in-memory cache and its durable graph backend. Zero for node-scoped erasures (which do
    /// not touch cross-session memory) and when no cross-session memory store is configured.
    /// Defaults to zero so pre-existing construction sites remain valid.
    /// </summary>
    public int CrossSessionMemoriesDeleted { get; init; }

    /// <summary>
    /// Whether the erasure fulfilled its full declared scope. Defaults to
    /// <see cref="ErasureCompleteness.Full"/> so pre-existing construction sites remain valid;
    /// the orchestrator downgrades it to <see cref="ErasureCompleteness.Partial"/> and populates
    /// <see cref="CompletenessReason"/> when a scoped sweep could not run.
    /// </summary>
    public ErasureCompleteness Completeness { get; init; } = ErasureCompleteness.Full;

    /// <summary>
    /// Human-readable explanation of what was left unpurged when <see cref="Completeness"/> is
    /// <see cref="ErasureCompleteness.Partial"/>; <see langword="null"/> when the erasure was
    /// <see cref="ErasureCompleteness.Full"/>.
    /// </summary>
    public string? CompletenessReason { get; init; }
}
