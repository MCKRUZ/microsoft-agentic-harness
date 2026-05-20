namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A detected community of related <see cref="GraphNode"/> entities in the knowledge graph,
/// produced by the Leiden algorithm. Communities enable hierarchical summarization for
/// GraphRAG global search — each community at a given level gets a summary that captures
/// the collective theme of its member nodes.
/// </summary>
public sealed record Community
{
    /// <summary>Unique identifier for this community, typically "community_{level}_{index}".</summary>
    public required string Id { get; init; }

    /// <summary>The hierarchy level (0 = most granular, higher = broader).</summary>
    public required int Level { get; init; }

    /// <summary>LLM-generated summary describing this community's theme.</summary>
    public required string Summary { get; init; }

    /// <summary>The IDs of GraphNode entities belonging to this community.</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }

    /// <summary>Modularity score (0.0–1.0), measuring internal cohesion vs external connections.</summary>
    public required double Modularity { get; init; }
}
