using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.KnowledgeGraph;

/// <summary>
/// Applies provenance metadata to extracted entities via <see cref="IProvenanceStamper"/>.
/// Each node and edge receives a <see cref="Domain.AI.KnowledgeGraph.Models.ProvenanceStamp"/>
/// recording the source pipeline, extraction task, and timestamp for audit trail purposes.
/// </summary>
/// <remarks>
/// When provenance is disabled in configuration, <see cref="IProvenanceStamper.StampNode"/>
/// and <see cref="IProvenanceStamper.StampEdge"/> return the input entities unchanged,
/// so this executor becomes a passthrough without requiring conditional logic.
/// </remarks>
public sealed class StampProvenanceExecutor(
    IProvenanceStamper provenanceStamper,
    ILogger<StampProvenanceExecutor> logger)
    : Executor<ExtractedEntities, StampedEntities>("stamp_provenance")
{
    /// <summary>
    /// Creates a provenance stamp and applies it to all extracted nodes and edges.
    /// </summary>
    /// <param name="message">The extracted entities to stamp.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stamped entities ready for graph storage.</returns>
    public override ValueTask<StampedEntities> HandleAsync(
        ExtractedEntities message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var stamp = provenanceStamper.CreateStamp(
            sourcePipeline: message.SourcePipeline,
            sourceTask: "entity_extraction");

        var stampedNodes = message.Nodes
            .Select(n => provenanceStamper.StampNode(n, stamp))
            .ToList();

        var stampedEdges = message.Edges
            .Select(e => provenanceStamper.StampEdge(e, stamp))
            .ToList();

        logger.LogInformation(
            "Provenance stamped: {NodeCount} nodes, {EdgeCount} edges",
            stampedNodes.Count, stampedEdges.Count);

        var result = new StampedEntities(
            stampedNodes, stampedEdges,
            message.ChunksProcessed, message.SourcePipeline);

        return ValueTask.FromResult(result);
    }
}
