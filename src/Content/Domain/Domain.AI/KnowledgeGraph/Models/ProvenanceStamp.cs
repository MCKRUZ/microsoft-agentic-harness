namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Audit trail metadata stamped on every <see cref="GraphNode"/> and <see cref="GraphEdge"/>
/// during knowledge graph construction. Tracks the full lineage of how an entity or
/// relationship was extracted — which pipeline produced it, which task within the pipeline,
/// and with what confidence.
/// </summary>
/// <remarks>
/// <para>
/// Inspired by Cognee's <c>_stamp_provenance_deep</c> pattern, extended with
/// <see cref="SourceDocumentId"/>, <see cref="ExtractionConfidence"/>, and
/// <see cref="LastModifiedBy"/> for enterprise audit requirements.
/// </para>
/// <para>
/// Provenance stamps are immutable once created. When a node or edge is re-extracted
/// (e.g., during re-indexing), a new stamp replaces the old one via record with-expression.
/// </para>
/// </remarks>
public record ProvenanceStamp
{
    /// <summary>
    /// The name of the pipeline that produced this entity/relationship
    /// (e.g., "rag_ingestion", "knowledge_enrichment").
    /// </summary>
    public required string SourcePipeline { get; init; }

    /// <summary>
    /// The specific task within the pipeline that performed the extraction
    /// (e.g., "entity_extraction", "relationship_detection", "ontology_validation").
    /// </summary>
    public required string SourceTask { get; init; }

    /// <summary>
    /// When this entity/relationship was extracted or last re-extracted.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The ID of the source document from which this entity/relationship was extracted.
    /// Null when the entity was synthesized (e.g., from community summarization).
    /// </summary>
    public string? SourceDocumentId { get; init; }

    /// <summary>
    /// The LLM's confidence in the extraction, normalized to 0.0–1.0.
    /// Null when confidence scoring is not available for the extraction method.
    /// </summary>
    public double? ExtractionConfidence { get; init; }

    /// <summary>
    /// Identifier of the user or system that last modified this entity/relationship.
    /// Populated from <see cref="IKnowledgeScope"/> during graph construction.
    /// </summary>
    public string? LastModifiedBy { get; init; }
}
