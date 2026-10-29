using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;
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
    private readonly Func<IGraphDatabaseBackend?> _corpusGraph;
    private readonly IAmbientRequestScope? _ambientScope;

    /// <summary>A document id as every chunker forms it: the first 16 upper-case hex digits of a SHA-256.</summary>
    private static readonly Regex DocumentIdFormat = new(
        "^[0-9A-F]{16}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
    /// Resolves the optional GraphRAG corpus graph, a store of its own beside the knowledge graph, or
    /// <see langword="null"/> when the graph database is off. Resolved only when a document is removed, so a corpus graph
    /// that cannot open never stops a person's erasure.
    /// </param>
    /// <param name="ambientScope">
    /// Suspended while a document is removed, so a tenant-isolated graph shows every tenant's knowledge drawn from it.
    /// <see langword="null"/> where no request scope is ever set.
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
        Func<IGraphDatabaseBackend?>? corpusGraph = null,
        IAmbientRequestScope? ambientScope = null)
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
        _corpusGraph = corpusGraph ?? (() => null);
        _ambientScope = ambientScope;
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
        if (!DocumentIdFormat.IsMatch(documentId))
            throw new ArgumentException($"'{documentId}' is not a document id.", nameof(documentId));

        // A document id is the same for every tenant that loaded the source, so this runs system-scoped, as erasure does:
        // a tenant-isolated graph would otherwise show only the caller's tenant and the receipt would still read Full.
        using var systemScoped = _ambientScope?.SuspendScope();

        var requestedAt = _timeProvider.GetUtcNow();
        var requestId = Guid.NewGuid().ToString();
        var partialReasons = new List<string>();
        var notConfigured = NotConfiguredChunkStores();
        var knowledge = GraphRemoval.None;
        var corpus = GraphRemoval.None;
        var communitiesDeleted = 0;
        var weightsDeleted = 0;
        var vectorDeleted = 0;
        var bm25Deleted = 0;
        try
        {
            knowledge = await RemoveDocumentFromGraphAsync(_graphStore, documentId, cancellationToken);
            var corpusGraph = _corpusGraph();
            if (corpusGraph is not null && !ReferenceEquals(corpusGraph, _graphStore))
            {
                corpus = await RemoveDocumentFromGraphAsync(corpusGraph, documentId, cancellationToken);
                if (corpus.ChangedNodeIds.Count > 0)
                    communitiesDeleted = await corpusGraph.DeleteCommunitiesContainingAsync(corpus.ChangedNodeIds, cancellationToken);
            }

            var nodeIds = DeletedNodeIds(knowledge, corpus);
            var edgeIds = DeletedEdgeIds(knowledge, corpus);
            weightsDeleted =
                (nodeIds.Count > 0 ? await _feedbackStore.DeleteWeightsByNodeIdsAsync(nodeIds, cancellationToken) : 0)
                + (edgeIds.Count > 0 ? await _feedbackStore.DeleteWeightsByEdgeIdsAsync(edgeIds, cancellationToken) : 0);

            vectorDeleted = await DeleteChunksAsync(
                "vector store", _vectorStore, (store, ct) => store.DeleteFromAllCollectionsAsync(documentId, ct),
                partialReasons, cancellationToken);
            bm25Deleted = await DeleteChunksAsync(
                "full-text store", _bm25Store, (store, ct) => store.DeleteFromAllCollectionsAsync(documentId, ct),
                partialReasons, cancellationToken);
        }
        finally
        {
            // Recorded whatever happened: a removal that failed part-way has still changed the stores. The document id is
            // the hash of where the document came from, not anyone's identity.
            await _auditSink.EmitAsync(new MemoryAuditEvent
            {
                EventId = requestId,
                Action = MemoryAuditAction.DocumentRemoval,
                ActorId = ErasureAuditScope,
                Timestamp = _timeProvider.GetUtcNow(),
                ScopeId = ErasureAuditScope,
                AffectedNodeCount = DeletedNodeIds(knowledge, corpus).Count,
                AffectedEdgeCount = DeletedEdgeIds(knowledge, corpus).Count
            }, CancellationToken.None);
        }

        var deletedNodes = DeletedNodeIds(knowledge, corpus).Count;
        var deletedEdges = DeletedEdgeIds(knowledge, corpus).Count;
        var trimmedNodes = knowledge.TrimmedNodes + corpus.TrimmedNodes;
        var matchedNothing = vectorDeleted == 0 && bm25Deleted == 0 && deletedNodes == 0 && deletedEdges == 0
            && trimmedNodes == 0 && partialReasons.Count == 0;

        var receipt = new ErasureReceipt
        {
            RequestId = requestId,
            ScopeId = "document",
            RequestedAt = requestedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            NodesDeleted = deletedNodes,
            EdgesDeleted = deletedEdges,
            FeedbackWeightsDeleted = weightsDeleted,
            VectorEmbeddingsDeleted = vectorDeleted,
            Bm25DocumentsDeleted = bm25Deleted,
            UnmatchedDocumentIds = matchedNothing ? [documentId] : [],
            Completeness = partialReasons.Count == 0 ? ErasureCompleteness.Full : ErasureCompleteness.Partial,
            CompletenessReason = partialReasons.Count == 0 ? null : string.Join("; ", partialReasons),
            Stores = notConfigured,
        };

        _logger.LogInformation(
            "Document removal completed: RequestId={RequestId}, Completeness={Completeness}, Nodes={Nodes}, " +
            "TrimmedNodes={Trimmed}, Edges={Edges}, Communities={Communities}, FeedbackWeights={FeedbackWeights}, " +
            "VectorChunks={VectorChunks}, Bm25Rows={Bm25Rows}, Matched={Matched}",
            requestId, receipt.Completeness, deletedNodes, trimmedNodes, deletedEdges, communitiesDeleted, weightsDeleted,
            vectorDeleted, bm25Deleted, !matchedNothing);

        return receipt;
    }

    /// <summary>
    /// Deletes the document's chunks from one chunk store, reporting a failure on the receipt instead of losing what the
    /// graph steps already did. A store this host has not configured is not asked (#1011); <see cref="NotConfiguredChunkStores"/>
    /// names it on the receipt instead.
    /// </summary>
    private async Task<int> DeleteChunksAsync<TStore>(
        string label, TStore? store, Func<TStore, CancellationToken, Task<int>> delete, List<string> partialReasons,
        CancellationToken cancellationToken)
        where TStore : class
    {
        if (store is null || IsUnconfigured(store))
            return 0;

        try
        {
            return await delete(store, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Document removal could not delete the document's chunks from the {Store}.", label);
            partialReasons.Add($"the {label} could not delete the document's chunks");
            return 0;
        }
    }

    /// <summary>
    /// Whether a registered chunk store is one this host left without a backend (#1003). It has never held anything, so it
    /// is not asked and can neither delete a document nor fail to find one (#1011).
    /// </summary>
    private static bool IsUnconfigured(object store) => store is IConfigurablePersonalDataStore { IsConfigured: false };

    /// <summary>
    /// A <see cref="StoreErasureStatus.NotConfigured"/> line for each registered chunk store this host has not configured,
    /// so a receipt does not read "0 removed" for a store that was never asked, which looks like a search that found
    /// nothing (#1011). It leaves completeness alone, as the erasure coordinator does.
    /// </summary>
    private List<StoreErasureOutcome> NotConfiguredChunkStores() =>
    [
        .. new object?[] { _vectorStore, _bm25Store }
            .OfType<object>()
            .Where(IsUnconfigured)
            .Select(store => new StoreErasureOutcome(
                store is IPersonalDataEraser eraser ? eraser.ErasureStoreName : store.GetType().Name,
                StoreErasureStatus.NotConfigured, 0, IConfigurablePersonalDataStore.NotConfiguredReason)),
    ];

    private static IReadOnlyList<string> DeletedNodeIds(GraphRemoval knowledge, GraphRemoval corpus) =>
        knowledge.DeletedNodeIds.Concat(corpus.DeletedNodeIds).Distinct(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> DeletedEdgeIds(GraphRemoval knowledge, GraphRemoval corpus) =>
        knowledge.DeletedEdgeIds.Concat(corpus.DeletedEdgeIds).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Takes one document out of one graph. Its chunk references are removed from every node that holds them, as one
    /// operation per node against the stored list, so a reference another document merges in meanwhile is kept; a node left
    /// with none is then deleted, re-read just before so a node another document has just claimed survives. Edges drawn
    /// from its text are deleted. Which chunk belongs to which document is decided here, by <see cref="DeriveDocumentId"/>,
    /// for every backend alike.
    /// </summary>
    /// <remarks>
    /// An edge records one chunk, the first document to assert the relationship (the last, in Neo4j). When another
    /// document asserts the same relationship, removing the recorded one deletes it anyway; loading the other document
    /// again restores it. A document loaded again while it is being removed can be partly restored.
    /// </remarks>
    private static async Task<GraphRemoval> RemoveDocumentFromGraphAsync(
        IKnowledgeGraphStore graph, string documentId, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        var trimmed = 0;
        foreach (var node in await graph.GetAllNodesAsync(cancellationToken))
        {
            var fromDocument = node.ChunkIds.Where(c => FromDocument(c, documentId)).ToList();
            if (fromDocument.Count == 0)
                continue;

            changed.Add(node.Id);
            await graph.RemoveChunkIdsAsync(node.Id, fromDocument, cancellationToken);
            if (fromDocument.Count < node.ChunkIds.Count)
                trimmed++;
        }

        var toDelete = new List<string>();
        foreach (var nodeId in changed)
        {
            if (await graph.GetNodeAsync(nodeId, cancellationToken) is { ChunkIds.Count: 0 })
                toDelete.Add(nodeId);
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

    /// <summary>
    /// Whether a chunk id is one of <paramref name="documentId"/>'s: it carries a chunk or RAPTOR marker and the id before
    /// it is exactly the document's. Fixed ids such as the learnings index's carry no marker and are never a document's.
    /// </summary>
    private static bool FromDocument(string? chunkId, string documentId) =>
        !string.IsNullOrEmpty(chunkId) && HasChunkMarker(chunkId)
        && string.Equals(DeriveDocumentId(chunkId), documentId, StringComparison.Ordinal);

    private static bool HasChunkMarker(string chunkId) =>
        chunkId.LastIndexOf("_chunk_", StringComparison.Ordinal) > 0
        || chunkId.LastIndexOf("_raptor_", StringComparison.Ordinal) > 0;

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
        // A store with no backend configured is not asked and cannot leave a document unmatched (#1011); the receipt
        // names it NotConfigured instead.
        var vectors = _vectorStore is { } v && !IsUnconfigured(v) ? v : null;
        var bm25 = _bm25Store is { } b && !IsUnconfigured(b) ? b : null;
        var notConfigured = purgeDocumentContent ? NotConfiguredChunkStores() : [];
        foreach (var documentId in documentIds)
        {
            var vectorDeleted = vectors is null ? 0 : await vectors.DeleteFromAllCollectionsAsync(documentId, cancellationToken);
            var bm25Deleted = bm25 is null ? 0 : await bm25.DeleteFromAllCollectionsAsync(documentId, cancellationToken);

            vectorChunksDeleted += vectorDeleted;
            bm25RowsDeleted += bm25Deleted;

            if ((vectors is not null || bm25 is not null) && vectorDeleted == 0 && bm25Deleted == 0)
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
            Stores = notConfigured,
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
