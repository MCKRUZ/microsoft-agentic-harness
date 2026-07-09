using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Coordinates right-to-erasure across every storage layer that can retain a subject's data:
/// graph nodes/edges, feedback weights, derived vector + BM25 (including RAPTOR summary) content,
/// and cross-session memory. Produces an <see cref="ErasureReceipt"/> as proof of compliance, and
/// records honestly — via <see cref="ErasureReceipt.Completeness"/> — when a scoped sweep could
/// not run rather than presenting a partial erasure as a clean success.
/// </summary>
public sealed class DefaultErasureOrchestrator : IErasureOrchestrator
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IFeedbackStore _feedbackStore;
    private readonly IVectorStore? _vectorStore;
    private readonly IBm25Store? _bm25Store;
    private readonly ICrossSessionMemoryStore? _crossSessionMemory;
    private readonly IMemoryAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultErasureOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultErasureOrchestrator"/> class.
    /// </summary>
    /// <param name="graphStore">The knowledge graph store holding nodes and edges.</param>
    /// <param name="feedbackStore">The feedback-weight store keyed by node/edge id.</param>
    /// <param name="vectorStore">
    /// Optional vector store. <see langword="null"/> when no vector backend is deployed, in which
    /// case there are no embeddings to purge.
    /// </param>
    /// <param name="auditSink">The audit sink that records the erasure as compliance proof.</param>
    /// <param name="timeProvider">Time source for receipt timestamps.</param>
    /// <param name="logger">Logger for recording erasure activity.</param>
    /// <param name="bm25Store">
    /// Optional BM25/full-text store. <see langword="null"/> when no BM25 backend is deployed, in
    /// which case there are no sparse rows (nor RAPTOR summary rows) to purge.
    /// </param>
    /// <param name="crossSessionMemory">
    /// Optional cross-session memory store. <see langword="null"/> when cross-session memory is
    /// not enabled, in which case there is no memory to purge. Only ever swept on the owner path.
    /// </param>
    public DefaultErasureOrchestrator(
        IKnowledgeGraphStore graphStore,
        IFeedbackStore feedbackStore,
        IVectorStore? vectorStore,
        IMemoryAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<DefaultErasureOrchestrator> logger,
        IBm25Store? bm25Store = null,
        ICrossSessionMemoryStore? crossSessionMemory = null)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _feedbackStore = feedbackStore;
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _crossSessionMemory = crossSessionMemory;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ErasureReceipt> EraseByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var requestedAt = _timeProvider.GetUtcNow();
        var requestId = Guid.NewGuid().ToString();

        // Canonicalize the owner ONCE at the entry point and drive every downstream sweep — graph
        // nodes, owner-scoped edges, and cross-session memory — off the single canonical value.
        // Graph owner matching is case-sensitive, so mixing raw and canonical owners could leave
        // graph data behind while the receipt still reported Full (the exact silent-survival case
        // ScopeIdentity guards against). When the owner canonicalizes to null the scope is
        // unresolvable: skip every owner-scoped sweep uniformly and report Partial honestly.
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);

        var nodes = canonicalOwner is not null
            ? await _graphStore.GetNodesByOwnerAsync(canonicalOwner, cancellationToken)
            : [];
        var nodeIds = nodes.Select(n => n.Id).ToList();

        // Prefer the canonical owner as the receipt/audit scope; fall back to the raw input for
        // traceability when it canonicalizes to null.
        var scopeId = canonicalOwner ?? ownerId;

        return await ExecuteErasureAsync(
            requestId, scopeId, canonicalOwner, ownerScopeRequested: true,
            nodes, nodeIds, requestedAt, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ErasureReceipt> EraseByNodeIdsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var requestedAt = _timeProvider.GetUtcNow();
        var requestId = Guid.NewGuid().ToString();

        var nodes = new List<GraphNode>();
        foreach (var nodeId in nodeIds)
        {
            var node = await _graphStore.GetNodeAsync(nodeId, cancellationToken);
            if (node is not null) nodes.Add(node);
        }

        var scopeId = nodes.FirstOrDefault()?.OwnerId ?? "system";
        // Node-scoped erasure has no owner to sweep edges/memory for; the node cascade covers it.
        return await ExecuteErasureAsync(
            requestId, scopeId, ownerId: null, ownerScopeRequested: false,
            nodes, nodeIds.ToList(), requestedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ErasureReceipt> EraseByNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        var requestedAt = _timeProvider.GetUtcNow();
        var requestId = Guid.NewGuid().ToString();

        // Trust the caller's node instances (they hold the ChunkIds the compliance-aware store
        // would return null for on re-fetch). Node-scoped: no owner sweep.
        var scopeId = nodes.FirstOrDefault()?.OwnerId ?? "system";
        var nodeIds = nodes.Select(n => n.Id).ToList();
        return ExecuteErasureAsync(
            requestId, scopeId, ownerId: null, ownerScopeRequested: false,
            nodes, nodeIds, requestedAt, cancellationToken);
    }

    private async Task<ErasureReceipt> ExecuteErasureAsync(
        string requestId,
        string scopeId,
        string? ownerId,
        bool ownerScopeRequested,
        IReadOnlyList<GraphNode> nodes,
        List<string> nodeIds,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        var partialReasons = new List<string>();

        // 1. Delete graph nodes and their connected edges. The store reports what it
        //    actually removed — the receipt must never echo the requested counts.
        var nodeDeletion = nodeIds.Count > 0
            ? await _graphStore.DeleteNodesAsync(nodeIds, cancellationToken)
            : NodeDeletionResult.Empty;

        // 2. Delete edges owned by the erased subject. The node cascade only removes edges
        //    touching the subject's nodes; edges the subject created between SURVIVING nodes
        //    would otherwise outlive the erasure.
        var ownerEdgeIds = ownerId is not null
            ? await _graphStore.DeleteEdgesByOwnerAsync(ownerId, cancellationToken)
            : [];

        var deletedEdgeIds = nodeDeletion.DeletedEdgeIds
            .Concat(ownerEdgeIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 3. Purge feedback weights for every erased node AND edge — dangling feedback both
        //    leaks signal about erased data and skews feedback-weighted retrieval.
        var nodeWeightsDeleted = nodeIds.Count > 0
            ? await _feedbackStore.DeleteWeightsByNodeIdsAsync(nodeIds, cancellationToken)
            : 0;
        var edgeWeightsDeleted = deletedEdgeIds.Count > 0
            ? await _feedbackStore.DeleteWeightsByEdgeIdsAsync(deletedEdgeIds, cancellationToken)
            : 0;

        // 4. Purge derived content (vector embeddings + BM25/full-text, including RAPTOR summary
        //    rows) by DOCUMENT id. Vector/BM25 delete by documentId, NOT chunkId — chunk IDs are
        //    "{documentId}_chunk_{i}" / "{documentId}_raptor_L{l}_C{c}", so we derive the document
        //    prefix. RAPTOR rows share the parent document id and drop out via the same delete.
        var documentIds = nodes
            .SelectMany(n => n.ChunkIds)
            .Select(DeriveDocumentId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var vectorDocsDeleted = 0;
        var bm25DocsDeleted = 0;
        if (documentIds.Count > 0)
        {
            if (_vectorStore is not null)
            {
                foreach (var documentId in documentIds)
                {
                    await _vectorStore.DeleteAsync(documentId, cancellationToken: cancellationToken);
                    vectorDocsDeleted++;
                }
            }

            if (_bm25Store is not null)
            {
                foreach (var documentId in documentIds)
                {
                    await _bm25Store.DeleteAsync(documentId, cancellationToken: cancellationToken);
                    bm25DocsDeleted++;
                }
            }
        }

        // 5. Purge cross-session memory owned by the subject. Only meaningful on the owner path
        //    (memory is owner-scoped, not node-scoped) and when the subsystem is deployed.
        var crossSessionDeleted = 0;
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);
        if (ownerScopeRequested && canonicalOwner is not null && _crossSessionMemory is not null)
        {
            crossSessionDeleted = await _crossSessionMemory
                .PurgeByOwnerAsync(canonicalOwner, cancellationToken);
        }

        // Completeness: an owner-scoped request whose owner could not be resolved cannot sweep
        // owner-scoped edges or cross-session memory, so it must NOT report as a clean success.
        if (ownerScopeRequested && canonicalOwner is null)
        {
            partialReasons.Add(
                "owner scope could not be resolved from an empty/whitespace owner id; " +
                "owner-scoped edge and cross-session-memory sweeps were skipped");
        }

        var completeness = partialReasons.Count == 0
            ? ErasureCompleteness.Full
            : ErasureCompleteness.Partial;
        var completenessReason = partialReasons.Count == 0
            ? null
            : string.Join("; ", partialReasons);

        var receipt = new ErasureReceipt
        {
            RequestId = requestId,
            ScopeId = scopeId,
            RequestedAt = requestedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            NodesDeleted = nodeDeletion.NodesDeleted,
            EdgesDeleted = deletedEdgeIds.Count,
            FeedbackWeightsDeleted = nodeWeightsDeleted + edgeWeightsDeleted,
            VectorEmbeddingsDeleted = vectorDocsDeleted,
            Bm25DocumentsDeleted = bm25DocsDeleted,
            CrossSessionMemoriesDeleted = crossSessionDeleted,
            Completeness = completeness,
            CompletenessReason = completenessReason
        };

        // 6. Emit audit event covering everything that was ACTUALLY erased — the deleted
        //    IDs reported by the stores, never the requested IDs. (The event itself is
        //    always emitted: it is the proof the erasure REQUEST was processed, receipt
        //    and all, even when nothing matched.)
        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = requestId,
            Action = MemoryAuditAction.Erasure,
            ActorId = scopeId,
            Timestamp = receipt.CompletedAt,
            ScopeId = scopeId,
            AffectedNodeIds = nodeDeletion.DeletedNodeIds,
            AffectedEdgeIds = deletedEdgeIds
        }, cancellationToken);

        _logger.LogInformation(
            "Erasure completed: RequestId={RequestId}, Completeness={Completeness}, Nodes={Nodes}, " +
            "Edges={Edges}, FeedbackWeights={FeedbackWeights}, VectorDocs={VectorDocs}, " +
            "Bm25Docs={Bm25Docs}, CrossSessionMemories={CrossSessionMemories}{Reason}",
            requestId, completeness, receipt.NodesDeleted, receipt.EdgesDeleted,
            receipt.FeedbackWeightsDeleted, vectorDocsDeleted, bm25DocsDeleted, crossSessionDeleted,
            completenessReason is null ? string.Empty : $", Reason={completenessReason}");

        return receipt;
    }

    /// <summary>
    /// Derives the document id from a chunk id. Chunk ids are
    /// <c>"{documentId}_chunk_{i}"</c> or <c>"{documentId}_raptor_L{l}_C{c}"</c>; the document id
    /// is the prefix before the rightmost marker. Because a document id may itself contain
    /// underscores (or even a literal <c>"_chunk_"</c>), the split is on the <em>last</em>
    /// occurrence of either marker, whichever sits further right. A chunk id with no marker
    /// (unexpected for corpus content) is returned unchanged — a delete-by that id is a harmless
    /// no-op if no such document exists.
    /// </summary>
    private static string DeriveDocumentId(string chunkId)
    {
        var chunkMarker = chunkId.LastIndexOf("_chunk_", StringComparison.Ordinal);
        var raptorMarker = chunkId.LastIndexOf("_raptor_", StringComparison.Ordinal);
        var cut = Math.Max(chunkMarker, raptorMarker);
        return cut > 0 ? chunkId[..cut] : chunkId;
    }
}
