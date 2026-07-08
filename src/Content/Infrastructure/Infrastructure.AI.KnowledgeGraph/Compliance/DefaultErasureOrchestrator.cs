using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Coordinates right-to-erasure across graph, feedback, and vector stores.
/// Produces an <see cref="ErasureReceipt"/> as proof of compliance.
/// </summary>
public sealed class DefaultErasureOrchestrator : IErasureOrchestrator
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IFeedbackStore _feedbackStore;
    private readonly IVectorStore? _vectorStore;
    private readonly IMemoryAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultErasureOrchestrator> _logger;

    public DefaultErasureOrchestrator(
        IKnowledgeGraphStore graphStore,
        IFeedbackStore feedbackStore,
        IVectorStore? vectorStore,
        IMemoryAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<DefaultErasureOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _feedbackStore = feedbackStore;
        _vectorStore = vectorStore;
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

        var nodes = await _graphStore.GetNodesByOwnerAsync(ownerId, cancellationToken);
        var nodeIds = nodes.Select(n => n.Id).ToList();

        return await ExecuteErasureAsync(
            requestId, ownerId, ownerId, nodes, nodeIds, requestedAt, cancellationToken);
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
        // Node-scoped erasure has no owner to sweep edges for; the node cascade covers it.
        return await ExecuteErasureAsync(
            requestId, scopeId, ownerId: null, nodes, nodeIds.ToList(), requestedAt, cancellationToken);
    }

    private async Task<ErasureReceipt> ExecuteErasureAsync(
        string requestId,
        string scopeId,
        string? ownerId,
        IReadOnlyList<GraphNode> nodes,
        List<string> nodeIds,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
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

        // 4. Delete vector embeddings (optional — not all deployments use vectors).
        //    IVectorStore.DeleteAsync returns no per-item confirmation, so this count is the
        //    number of chunk IDs submitted for deletion (each call either succeeds or throws).
        var chunkIds = nodes.SelectMany(n => n.ChunkIds).Distinct().ToList();
        var embeddingsDeleted = 0;
        if (_vectorStore is not null && chunkIds.Count > 0)
        {
            // IVectorStore does not yet have DeleteByDocumentIdsAsync (batch).
            // When added, replace the loop below with a single batch call.
            foreach (var chunkId in chunkIds)
            {
                await _vectorStore.DeleteAsync(chunkId, cancellationToken: cancellationToken);
                embeddingsDeleted++;
            }
        }

        var receipt = new ErasureReceipt
        {
            RequestId = requestId,
            ScopeId = scopeId,
            RequestedAt = requestedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            NodesDeleted = nodeDeletion.NodesDeleted,
            EdgesDeleted = deletedEdgeIds.Count,
            FeedbackWeightsDeleted = nodeWeightsDeleted + edgeWeightsDeleted,
            VectorEmbeddingsDeleted = embeddingsDeleted
        };

        // 5. Emit audit event covering everything that was ACTUALLY erased — the deleted
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
            "Erasure completed: RequestId={RequestId}, Nodes={Nodes}, Edges={Edges}, " +
            "FeedbackWeights={FeedbackWeights}, Embeddings={Embeddings}",
            requestId, receipt.NodesDeleted, receipt.EdgesDeleted,
            receipt.FeedbackWeightsDeleted, embeddingsDeleted);

        return receipt;
    }
}
