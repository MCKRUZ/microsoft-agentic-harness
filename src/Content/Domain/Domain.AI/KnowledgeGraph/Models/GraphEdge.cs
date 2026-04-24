namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A directed relationship edge between two <see cref="GraphNode"/> entities in the
/// knowledge graph. Edges are extracted alongside entities during LLM-based entity
/// recognition and represent semantic relationships (e.g., "uses", "manages", "depends_on").
/// </summary>
/// <remarks>
/// <para>
/// Edges are always directed from <see cref="SourceNodeId"/> to <see cref="TargetNodeId"/>.
/// The <see cref="Predicate"/> describes the relationship type using a verb or verb phrase
/// in snake_case (e.g., "works_at", "implements", "authored_by").
/// </para>
/// <para>
/// Each edge carries a <see cref="ChunkId"/> reference to the specific document chunk
/// from which the relationship was extracted, enabling citation tracking back to source text.
/// </para>
/// </remarks>
public record GraphEdge
{
    /// <summary>
    /// Unique identifier for this edge. Typically a deterministic hash of
    /// <see cref="SourceNodeId"/> + <see cref="Predicate"/> + <see cref="TargetNodeId"/>
    /// to prevent duplicate edges between the same node pair with the same predicate.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The ID of the source <see cref="GraphNode"/> from which this relationship originates.
    /// </summary>
    public required string SourceNodeId { get; init; }

    /// <summary>
    /// The ID of the target <see cref="GraphNode"/> to which this relationship points.
    /// </summary>
    public required string TargetNodeId { get; init; }

    /// <summary>
    /// The relationship type as a verb or verb phrase in snake_case
    /// (e.g., "uses", "manages", "depends_on", "authored_by").
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Domain-specific metadata properties for this relationship. Stored as strings
    /// for portability across graph backends.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The ID of the document chunk from which this relationship was extracted.
    /// Enables citation tracking back to the source text.
    /// </summary>
    public required string ChunkId { get; init; }

    /// <summary>
    /// Audit trail metadata tracking how this relationship was extracted — which pipeline,
    /// task, document, and with what confidence. Null when provenance stamping is disabled
    /// via <c>GraphRagConfig.ProvenanceEnabled = false</c>.
    /// </summary>
    public ProvenanceStamp? Provenance { get; init; }
}
