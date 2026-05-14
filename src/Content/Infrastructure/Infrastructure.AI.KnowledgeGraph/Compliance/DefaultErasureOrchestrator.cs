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
            requestId, ownerId, nodes, nodeIds, requestedAt, cancellationToken);
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
        return await ExecuteErasureAsync(
            requestId, scopeId, nodes, nodeIds.ToList(), requestedAt, cancellationToken);
    }

    private async Task<ErasureReceipt> ExecuteErasureAsync(
        string requestId,
        string scopeId,
        IReadOnlyList<GraphNode> nodes,
        List<string> nodeIds,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        // 1. Delete graph nodes (DeleteNodeAsync also removes connected edges)
        foreach (var nodeId in nodeIds)
            await _graphStore.DeleteNodeAsync(nodeId, cancellationToken);

        // 2. Delete feedback weights
        if (nodeIds.Count > 0)
            await _feedbackStore.DeleteWeightsByNodeIdsAsync(nodeIds, cancellationToken);

        // 3. Delete vector embeddings (optional — not all deployments use vectors)
        var chunkIds = nodes.SelectMany(n => n.ChunkIds).Distinct().ToList();
        var embeddingsDeleted = 0;
        if (_vectorStore is not null && chunkIds.Count > 0)
        {
            // IVectorStore does not yet have DeleteByDocumentIdsAsync (batch).
            // When added, replace the loop below with a single batch call.
            // For now, use the existing DeleteAsync per-document method.
            foreach (var chunkId in chunkIds)
                await _vectorStore.DeleteAsync(chunkId, cancellationToken: cancellationToken);

            embeddingsDeleted = chunkIds.Count;
        }

        var receipt = new ErasureReceipt
        {
            RequestId = requestId,
            ScopeId = scopeId,
            RequestedAt = requestedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            NodesDeleted = nodeIds.Count,
            EdgesDeleted = 0,
            FeedbackWeightsDeleted = nodeIds.Count,
            VectorEmbeddingsDeleted = embeddingsDeleted
        };

        // 4. Emit audit event
        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = requestId,
            Action = MemoryAuditAction.Erasure,
            ActorId = scopeId,
            Timestamp = receipt.CompletedAt,
            ScopeId = scopeId,
            AffectedNodeIds = nodeIds
        }, cancellationToken);

        _logger.LogInformation(
            "Erasure completed: RequestId={RequestId}, Nodes={Nodes}, Embeddings={Embeddings}",
            requestId, nodeIds.Count, embeddingsDeleted);

        return receipt;
    }
}
