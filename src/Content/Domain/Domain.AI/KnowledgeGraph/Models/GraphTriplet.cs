namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A complete subject-predicate-object triple from the knowledge graph, combining the
/// source <see cref="GraphNode"/>, connecting <see cref="GraphEdge"/>, and target
/// <see cref="GraphNode"/> into a single traversable unit.
/// </summary>
/// <remarks>
/// Triplets are the primary return type for graph queries (neighbor traversal,
/// subgraph extraction) and the unit of knowledge exchanged between the session
/// cache and permanent graph store during cross-session memory operations.
/// </remarks>
public record GraphTriplet
{
    /// <summary>
    /// The source entity node (subject of the triple).
    /// </summary>
    public required GraphNode Source { get; init; }

    /// <summary>
    /// The directed relationship edge connecting source to target.
    /// </summary>
    public required GraphEdge Edge { get; init; }

    /// <summary>
    /// The target entity node (object of the triple).
    /// </summary>
    public required GraphNode Target { get; init; }
}
