namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Defines the retention period for a specific entity type in the knowledge graph.
/// Used by <c>ComplianceAwareGraphStore</c> to compute <see cref="GraphNode.ExpiresAt"/>.
/// </summary>
public record RetentionPolicy
{
    /// <summary>The entity type this policy applies to (e.g., "Fact", "Concept").</summary>
    public required string EntityType { get; init; }
    /// <summary>How long entities of this type are retained before automatic purge.</summary>
    public required TimeSpan RetentionPeriod { get; init; }
    /// <summary>
    /// When <c>true</c>, entities of this type never expire regardless of
    /// <see cref="RetentionPeriod"/>. <see cref="GraphNode.ExpiresAt"/> will be null.
    /// </summary>
    public bool AllowIndefinite { get; init; }
}
