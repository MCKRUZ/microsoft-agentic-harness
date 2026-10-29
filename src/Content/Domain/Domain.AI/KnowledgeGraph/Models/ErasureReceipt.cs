using Domain.Common.Compliance;

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
    /// Number of chunk embeddings the vector store <em>actually removed</em>, summed across
    /// every collection (the erasure sweep uses the all-collections delete so per-tenant
    /// scoped collections are reached), as returned by
    /// <c>IVectorStore.DeleteFromAllCollectionsAsync</c>. Zero when no vector store is
    /// configured — and zero when nothing matched, never the submitted document count.
    /// </summary>
    public required int VectorEmbeddingsDeleted { get; init; }

    /// <summary>
    /// Number of BM25/full-text rows <em>actually removed</em>, summed across every
    /// collection, as returned by <c>IBm25Store.DeleteFromAllCollectionsAsync</c>. RAPTOR
    /// summary rows share the parent document ID and drop out via the same delete. Zero when
    /// no BM25 store is configured — and zero when nothing matched, never the submitted
    /// document count. Defaults to zero so pre-existing construction sites remain valid.
    /// </summary>
    public int Bm25DocumentsDeleted { get; init; }

    /// <summary>
    /// Document IDs that were derived from the erased nodes' chunk IDs and submitted to the
    /// derived-content sweep, but matched <em>zero</em> rows in every configured store. A
    /// non-empty list is the receipt's honest detail that content the graph manifest points
    /// at was not found where the stores looked — instead of silently counting those
    /// documents as purged. Empty when every submitted document matched rows or no
    /// vector/BM25 store is configured; a store registered with no backend counts as not
    /// configured and is not asked (#1011). Defaults to empty so pre-existing construction sites
    /// remain valid.
    /// </summary>
    public IReadOnlyList<string> UnmatchedDocumentIds { get; init; } = [];

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

    /// <summary>
    /// One line for every store beyond the knowledge graph that the request reached: conversations, plans, schedules,
    /// document indexes, and the stores found through the subject's conversations (#900). A store that failed, or was
    /// held back so a retry can still find what depends on it, makes <see cref="Completeness"/>
    /// <see cref="ErasureCompleteness.Partial"/>. Stores that hold no record of who their data belongs to cannot be
    /// searched for one person and are not listed here; the personal-data inventory records them. A receipt straight from
    /// the knowledge-graph erasure, and every document removal, lists only the chunk stores this host has not configured,
    /// as <see cref="StoreErasureStatus.NotConfigured"/> (#1011), so a store that was never asked does not read as one that
    /// searched and found nothing; the counts of the stores that were asked are <see cref="VectorEmbeddingsDeleted"/> and
    /// <see cref="Bm25DocumentsDeleted"/>, and the erasure coordinator replaces the list with its own lines. Defaults to
    /// empty so pre-existing construction sites remain valid.
    /// </summary>
    public IReadOnlyList<StoreErasureOutcome> Stores { get; init; } = [];

    /// <summary>
    /// The host process this receipt covers, such as <c>Presentation.AgentHub</c> or <c>Presentation.ExecutionApi</c>
    /// (#1003). An erasure reaches only the stores of the process that ran it, so <see cref="Completeness"/> speaks for
    /// that host alone: a deployment that runs several hosts holding personal data sends the request to each, and a person
    /// is erased everywhere only when every host's receipt reads <see cref="ErasureCompleteness.Full"/>. Set by the request
    /// path that hands the receipt out; <see langword="null"/> on a receipt taken straight from the erasure services.
    /// </summary>
    public string? Host { get; init; }
}
