namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Opt-in per-tenant isolation of the RAG document corpus. When enabled, the collection
/// that ingest writes to and search reads from is derived server-side from the caller's
/// ambient tenant (see <c>ScopedCollectionName</c> in <c>Domain.AI</c>), and any
/// caller-supplied collection name on an ingest or search request is rejected with a
/// validation failure — otherwise a caller could name another tenant's collection and turn
/// the parameter into a cross-tenant read primitive.
/// Bound from <c>AppConfig:AI:Rag:ScopedCollections</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default.</strong> When disabled, the corpus keeps today's deliberate
/// shared-corpus behavior: caller-supplied collection names are honored as before and
/// nothing about collection naming changes. One behavioral release-note applies
/// regardless of this flag: the SQLite FTS5 BM25 store now partitions by collection
/// <em>unconditionally</em> (it previously ignored collection names while the FAISS
/// vector store partitioned — a dense/sparse asymmetry that leaked across collections),
/// so flag-off callers who ingested under one collection name and searched under another
/// relied on that bug and will see the corrected, partitioned behavior.
/// </para>
/// <para>
/// <strong>Derivation.</strong> The effective collection is
/// <c>tenant-{slug}-{hash}</c> — a sanitized rendering of the ambient tenant id suffixed
/// with 16 hex characters (64 bits) of its SHA-256, deterministic and collision-safe
/// (trim + lowercase normalization). A caller with <em>no</em> ambient tenant resolves to
/// the global/default collection — closed and predictable, not an error: anonymous and
/// in-process callers share one well-known collection and can never reach a
/// tenant-derived one.
/// </para>
/// <para>
/// <strong>Enforcement.</strong> Belt and braces: the MediatR request validators reject
/// caller-supplied collection names, and the shared entry points every retrieval path
/// converges on (<c>RagOrchestrator.SearchAsync</c> and <c>HybridRetriever.RetrieveAsync</c>)
/// re-derive the collection from the ambient tenant regardless of what was passed in — so
/// agent tools, planner steps, and workflow executors that bypass the MediatR pipeline
/// cannot name another tenant's collection either. Resolution is idempotent, so the
/// double application is harmless.
/// </para>
/// <para>
/// <strong>Which retrieval sources honor scoping.</strong> The hybrid dense + sparse
/// pipeline honors it end-to-end with the local store pairing
/// (<c>VectorStore.Provider = "faiss"</c>: FAISS partitions by collection natively and the
/// SQLite FTS5 BM25 store partitions by a per-row collection value). The multi-source
/// orchestrator forwards the derived collection to its <c>"vector"</c> source. Sources
/// that are <em>not</em> collection-scoped and remain tenant-shared when enabled:
/// <list type="bullet">
///   <item><c>"graph"</c> — the corpus graph built by <c>GraphRag.IndexOnIngest</c> is a
///     single shared graph (the tenant-isolated knowledge graph store is a separate
///     surface with its own per-record isolation).</item>
///   <item><c>"web_search"</c> and <c>"sql_database"</c> — external sources with no
///     collection concept.</item>
/// </list>
/// Three combinations cannot honor scoping at all and are rejected at startup by
/// <c>RagConfigValidator</c> rather than allowed to leak silently:
/// <c>VectorStore.Provider = "azure_ai_search"</c> (both Azure stores query one
/// pre-provisioned index and ignore collection names; supporting scoping there requires
/// per-tenant index provisioning, which the harness deliberately leaves to
/// infrastructure), <c>AgenticRetrieval.Enabled = true</c> (the knowledge-base
/// retriever always queries the one configured knowledge base), and
/// <c>GraphRag.IndexOnIngest = true</c> (the corpus graph is one shared graph, so scoped
/// ingests would land every tenant's chunks in a graph readable by all).
/// </para>
/// </remarks>
public class ScopedCollectionsConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether per-tenant collection isolation is active.
    /// When <c>false</c> (default), the corpus is shared and caller-supplied collection
    /// names are honored unchanged. When <c>true</c>, collection names are derived
    /// server-side from the ambient tenant and caller-supplied names are rejected.
    /// </summary>
    public bool Enabled { get; set; }
}
