using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.KnowledgeGraph;

/// <summary>
/// Persists provenance-stamped entities to the knowledge graph backend via
/// <see cref="IKnowledgeGraphStore"/>. This is the final stage of the KG ingestion
/// workflow, writing extracted and stamped nodes and edges to the configured graph
/// database (in-memory, PostgreSQL, or Neo4j via keyed DI).
/// </summary>
/// <remarks>
/// Nodes are stored before edges to ensure referential integrity. The graph store
/// handles deduplication internally: duplicate node IDs trigger chunk ID merging,
/// and duplicate edge IDs are silently ignored.
/// </remarks>
public sealed class StoreGraphExecutor(
    IKnowledgeGraphStore graphStore,
    ILogger<StoreGraphExecutor> logger)
    : Executor<StampedEntities, KgIngestionResult>("store_graph")
{
    /// <summary>
    /// Stores the stamped nodes and edges in the knowledge graph backend.
    /// </summary>
    /// <param name="message">The provenance-stamped entities to persist.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ingestion result with counts of stored nodes and edges.</returns>
    public override async ValueTask<KgIngestionResult> HandleAsync(
        StampedEntities message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (message.Nodes.Count > 0)
        {
            await graphStore.AddNodesAsync(message.Nodes, cancellationToken);
        }

        if (message.Edges.Count > 0)
        {
            await graphStore.AddEdgesAsync(message.Edges, cancellationToken);
        }

        logger.LogInformation(
            "Graph storage completed: {NodeCount} nodes, {EdgeCount} edges stored",
            message.Nodes.Count, message.Edges.Count);

        return new KgIngestionResult(
            message.Nodes.Count,
            message.Edges.Count,
            message.ChunksProcessed);
    }
}
