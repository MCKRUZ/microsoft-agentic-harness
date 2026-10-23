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
    /// <summary>
    /// The actor and scope the <see cref="MemoryAuditAction.Erasure"/> event records. Erasure runs system-scoped, and the
    /// subject is the one identity the event must not carry.
    /// </summary>
    public const string ErasureAuditScope = "system";

    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IFeedbackStore _feedbackStore;
    private readonly IVectorStore? _vectorStore;
    private readonly IBm25Store? _bm25Store;
    private readonly ICrossSessionMemoryStore? _crossSessionMemory;
    private readonly IGraphDatabaseBackend? _corpusGraph;
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
    /// <param name="corpusGraph">
    /// Optional GraphRAG corpus graph, a store of its own beside the knowledge graph. <see langword="null"/> when the
    /// graph database is off. Only reached when a document is removed.
    /// </param>
    public DefaultErasureOrchestrator(
        IKnowledgeGraphStore graphStore,
        IFeedbackStore feedbackStore,
        IVectorStore? vectorStore,
        IMemoryAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<DefaultErasureOrchestrator> logger,
        IBm25Store? bm25Store = null,
        ICrossSessionMemoryStore? crossSessionMemory = null,
        IGraphDatabaseBackend? corpusGraph = null)
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
        _corpusGraph = corpusGraph;
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

    /// <inheritdoc />
    public Task<ErasureReceipt> ExpireNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var nodeIds = nodes.Select(n => n.Id).ToList();
        return ExecuteErasureAsync(
            Guid.NewGuid().ToString(), "retention", ownerId: null, ownerScopeRequested: false,
            nodes, nodeIds, _timeProvider.GetUtcNow(), cancellationToken, purgeDocumentContent: false);
    }

    /// <inheritdoc />
    public async Task<ErasureReceipt> RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var requestedAt = _timeProvider.GetUtcNow();
        var requestId = Guid.NewGuid().ToString();

        var knowledge = await RemoveDocumentFromGraphAsync(_graphStore, documentId, cancellationToken);
        var corpus = _corpusGraph is null || ReferenceEquals(_corpusGraph, _graphStore)
            ? GraphRemoval.None
            : await RemoveDocumentFromGraphAsync(_corpusGraph, documentId, cancellationToken);
        var communitiesDeleted = _corpusGraph is not null && corpus.ChangedNodeIds.Count > 0
            ? await _corpusGraph.DeleteCommunitiesContainingAsync(corpus.ChangedNodeIds, cancellationToken)
            : 0;

        var nodeIds = knowledge.DeletedNodeIds.Concat(corpus.DeletedNodeIds).ToList();
        var edgeIds = knowledge.DeletedEdgeIds.Concat(corpus.DeletedEdgeIds).Distinct(StringComparer.Ordinal).ToList();
        var weightsDeleted =
            (nodeIds.Count > 0 ? await _feedbackStore.DeleteWeightsByNodeIdsAsync(nodeIds, cancellationToken) : 0)
            + (edgeIds.Count > 0 ? await _feedbackStore.DeleteWeightsByEdgeIdsAsync(edgeIds, cancellationToken) : 0);

        var vectorDeleted = _vectorStore is not null
            ? await _vectorStore.DeleteFromAllCollectionsAsync(documentId, cancellationToken)
            : 0;
        var bm25Deleted = _bm25Store is not null
            ? await _bm25Store.DeleteFromAllCollectionsAsync(documentId, cancellationToken)
            : 0;
        var matchedNothing = vectorDeleted == 0 && bm25Deleted == 0 && nodeIds.Count == 0 && edgeIds.Count == 0
            && knowledge.TrimmedNodes + corpus.TrimmedNodes == 0;

        var receipt = new ErasureReceipt
        {
            RequestId = requestId,
            ScopeId = "document",
            RequestedAt = requestedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            NodesDeleted = nodeIds.Count,
            EdgesDeleted = edgeIds.Count,
            FeedbackWeightsDeleted = weightsDeleted,
            VectorEmbeddingsDeleted = vectorDeleted,
            Bm25DocumentsDeleted = bm25Deleted,
            UnmatchedDocumentIds = matchedNothing ? [documentId] : [],
            Completeness = ErasureCompleteness.Full,
        };

        // The document id is the hash of where the document came from, not anyone's identity.
        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = requestId,
            Action = MemoryAuditAction.DocumentRemoval,
            ActorId = ErasureAuditScope,
            Timestamp = receipt.CompletedAt,
            ScopeId = ErasureAuditScope,
            AffectedNodeCount = nodeIds.Count,
            AffectedEdgeCount = edgeIds.Count
        }, cancellationToken);

        _logger.LogInformation(
            "Document removal completed: RequestId={RequestId}, Nodes={Nodes}, TrimmedNodes={Trimmed}, Edges={Edges}, " +
            "Communities={Communities}, FeedbackWeights={FeedbackWeights}, VectorChunks={VectorChunks}, Bm25Rows={Bm25Rows}, " +
            "Matched={Matched}",
            requestId, nodeIds.Count, knowledge.TrimmedNodes + corpus.TrimmedNodes, edgeIds.Count, communitiesDeleted,
            weightsDeleted, vectorDeleted, bm25Deleted, !matchedNothing);

        return receipt;
    }

    /// <summary>
    /// Takes one document out of one graph: nodes only it produced are deleted, nodes it shares with another document lose
    /// its chunk references, and edges drawn from its text are deleted. Which chunk belongs to which document is decided
    /// here, by <see cref="DeriveDocumentId"/>, for every backend alike.
    /// </summary>
    private static async Task<GraphRemoval> RemoveDocumentFromGraphAsync(
        IKnowledgeGraphStore graph, string documentId, CancellationToken cancellationToken)
    {
        var toDelete = new List<string>();
        var changed = new List<string>();
        var trimmed = 0;
        foreach (var node in await graph.GetAllNodesAsync(cancellationToken))
        {
            var kept = node.ChunkIds.Where(c => !FromDocument(c, documentId)).ToList();
            if (kept.Count == node.ChunkIds.Count)
                continue;

            changed.Add(node.Id);
            if (kept.Count == 0)
            {
                toDelete.Add(node.Id);
            }
            else
            {
                await graph.ReplaceChunkIdsAsync(node.Id, kept, cancellationToken);
                trimmed++;
            }
        }

        var deletion = toDelete.Count > 0
            ? await graph.DeleteNodesAsync(toDelete, cancellationToken)
            : NodeDeletionResult.Empty;
        var cascaded = deletion.DeletedEdgeIds.ToHashSet(StringComparer.Ordinal);
        var edgeIds = new List<string>(deletion.DeletedEdgeIds);
        foreach (var edge in await graph.GetAllEdgesAsync(cancellationToken))
        {
            if (cascaded.Contains(edge.Id) || !FromDocument(edge.ChunkId, documentId))
                continue;

            await graph.DeleteEdgeAsync(edge.Id, cancellationToken);
            edgeIds.Add(edge.Id);
        }

        return new GraphRemoval(deletion.DeletedNodeIds.ToList(), edgeIds, changed, trimmed);
    }

    private static bool FromDocument(string? chunkId, string documentId) =>
        !string.IsNullOrEmpty(chunkId) && string.Equals(DeriveDocumentId(chunkId), documentId, StringComparison.Ordinal);

    /// <summary>What removing a document took out of one graph.</summary>
    private sealed record GraphRemoval(
        IReadOnlyList<string> DeletedNodeIds,
        IReadOnlyList<string> DeletedEdgeIds,
        IReadOnlyList<string> ChangedNodeIds,
        int TrimmedNodes)
    {
        public static readonly GraphRemoval None = new([], [], [], 0);
    }

    private async Task<ErasureReceipt> ExecuteErasureAsync(
        string requestId,
        string scopeId,
        string? ownerId,
        bool ownerScopeRequested,
        IReadOnlyList<GraphNode> nodes,
        List<string> nodeIds,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken,
        bool purgeDocumentContent = true)
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
        //    The sweep uses the ALL-collections delete: with ScopedCollections a document's
        //    chunks live in a tenant-derived collection, and a default-collection delete would
        //    remove nothing while the receipt still reported the purge — a compliance-grade
        //    over-report of a no-op. Counts come from the stores (rows actually removed), and a
        //    document that matched zero rows in every configured store is surfaced on the receipt
        //    instead of silently counting as done.
        //    Skipped for retention expiry (#901): an aged node must not take its whole source document with it.
        var documentIds = nodes
            .Where(_ => purgeDocumentContent)
            .SelectMany(n => n.ChunkIds)
            .Select(DeriveDocumentId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var vectorChunksDeleted = 0;
        var bm25RowsDeleted = 0;
        var unmatchedDocumentIds = new List<string>();
        foreach (var documentId in documentIds)
        {
            var vectorDeleted = _vectorStore is not null
                ? await _vectorStore.DeleteFromAllCollectionsAsync(documentId, cancellationToken)
                : 0;
            var bm25Deleted = _bm25Store is not null
                ? await _bm25Store.DeleteFromAllCollectionsAsync(documentId, cancellationToken)
                : 0;

            vectorChunksDeleted += vectorDeleted;
            bm25RowsDeleted += bm25Deleted;

            if ((_vectorStore is not null || _bm25Store is not null)
                && vectorDeleted == 0 && bm25Deleted == 0)
            {
                unmatchedDocumentIds.Add(documentId);
            }
        }

        // 5. Purge cross-session memory owned by the subject. Only meaningful on the owner path
        //    (memory is owner-scoped, not node-scoped) and when the subsystem is deployed.
        var crossSessionDeleted = 0;
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);
        if (ownerScopeRequested && canonicalOwner is not null && _crossSessionMemory is not null)
        {
            try
            {
                crossSessionDeleted = await _crossSessionMemory
                    .PurgeByOwnerAsync(canonicalOwner, cancellationToken);
            }
            catch (NotImplementedException)
            {
                // A backend that cannot actually delete anything (e.g. RemoteCrossSessionMemoryStore
                // when remote memory hosting is enabled) throws here rather than silently returning
                // 0 — see its own remarks. Downgrading to Partial, rather than letting the whole
                // request fail, is what keeps the graph/vector/BM25 sweeps above this one meaningful.
                partialReasons.Add(
                    "cross-session memory purge is not supported by the configured backend; the " +
                    "subject's cross-session-memory records were not erased");
            }
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
            VectorEmbeddingsDeleted = vectorChunksDeleted,
            Bm25DocumentsDeleted = bm25RowsDeleted,
            CrossSessionMemoriesDeleted = crossSessionDeleted,
            UnmatchedDocumentIds = unmatchedDocumentIds,
            Completeness = completeness,
            CompletenessReason = completenessReason
        };

        // 6. Emit audit event covering everything that was ACTUALLY erased — the counts the
        //    stores reported, never the requested ones. (The event itself is always emitted:
        //    it is the proof the erasure REQUEST was processed, receipt and all, even when
        //    nothing matched.) It names neither the subject nor any erased id: node ids embed
        //    the owner and memory keys, so recording them would keep what was erased (#900).
        //    The event id is the receipt's request id, which links it to the request's own
        //    audit record.
        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = requestId,
            // Retention expiry is recorded as such, so the trail still shows how erasure requests were handled.
            Action = purgeDocumentContent ? MemoryAuditAction.Erasure : MemoryAuditAction.Expiry,
            ActorId = ErasureAuditScope,
            Timestamp = receipt.CompletedAt,
            ScopeId = ErasureAuditScope,
            AffectedNodeCount = nodeDeletion.DeletedNodeIds.Count,
            AffectedEdgeCount = deletedEdgeIds.Count
        }, cancellationToken);

        _logger.LogInformation(
            "{Kind} completed: RequestId={RequestId}, Completeness={Completeness}, Nodes={Nodes}, " +
            "Edges={Edges}, FeedbackWeights={FeedbackWeights}, VectorChunks={VectorChunks}, " +
            "Bm25Rows={Bm25Rows}, CrossSessionMemories={CrossSessionMemories}, " +
            "UnmatchedDocuments={UnmatchedDocuments}{Reason}",
            purgeDocumentContent ? "Erasure" : "Retention expiry", requestId, completeness, receipt.NodesDeleted, receipt.EdgesDeleted,
            receipt.FeedbackWeightsDeleted, vectorChunksDeleted, bm25RowsDeleted, crossSessionDeleted,
            unmatchedDocumentIds.Count,
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
