using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Creates and applies <see cref="ProvenanceStamp"/> metadata to knowledge graph entities,
/// tracking the lineage of how each node and edge was extracted. Inspired by Cognee's
/// <c>_stamp_provenance_deep</c> pattern, extended with document-level attribution
/// and extraction confidence.
/// </summary>
/// <remarks>
/// <para>
/// Provenance stamping is controlled by <c>GraphRagConfig.ProvenanceEnabled</c>.
/// When disabled, <see cref="StampNode"/> and <see cref="StampEdge"/> return the
/// input entity unchanged (no stamp attached).
/// </para>
/// <para>
/// Implementations should be registered as singletons — stamps are created from
/// immutable context parameters and the current timestamp, with no mutable state.
/// </para>
/// </remarks>
public interface IProvenanceStamper
{
    /// <summary>
    /// Creates a new <see cref="ProvenanceStamp"/> for the given pipeline context.
    /// </summary>
    /// <param name="sourcePipeline">The pipeline producing the entity (e.g., "rag_ingestion").</param>
    /// <param name="sourceTask">The task within the pipeline (e.g., "entity_extraction").</param>
    /// <param name="sourceDocumentId">The source document ID, if applicable.</param>
    /// <param name="extractionConfidence">The LLM's extraction confidence (0.0–1.0), if available.</param>
    /// <param name="modifiedBy">The user or system performing the extraction.</param>
    ProvenanceStamp CreateStamp(
        string sourcePipeline,
        string sourceTask,
        string? sourceDocumentId = null,
        double? extractionConfidence = null,
        string? modifiedBy = null);

    /// <summary>
    /// Returns a new <see cref="GraphNode"/> with provenance metadata attached.
    /// When provenance is disabled, returns <paramref name="node"/> unchanged.
    /// </summary>
    /// <param name="node">The node to stamp.</param>
    /// <param name="stamp">The provenance stamp to apply.</param>
    GraphNode StampNode(GraphNode node, ProvenanceStamp stamp);

    /// <summary>
    /// Returns a new <see cref="GraphEdge"/> with provenance metadata attached.
    /// When provenance is disabled, returns <paramref name="edge"/> unchanged.
    /// </summary>
    /// <param name="edge">The edge to stamp.</param>
    /// <param name="stamp">The provenance stamp to apply.</param>
    GraphEdge StampEdge(GraphEdge edge, ProvenanceStamp stamp);
}
