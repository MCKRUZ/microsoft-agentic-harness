namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Immutable proof that a right-to-erasure request was fulfilled. Contains counts
/// of all entities deleted across graph, feedback, and vector stores.
/// </summary>
public record ErasureReceipt
{
    /// <summary>Unique identifier for this erasure request.</summary>
    public required string RequestId { get; init; }
    /// <summary>The scope (user/tenant) whose data was erased.</summary>
    public required string ScopeId { get; init; }
    /// <summary>When the erasure was requested.</summary>
    public required DateTimeOffset RequestedAt { get; init; }
    /// <summary>When the erasure completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
    /// <summary>Number of graph nodes deleted.</summary>
    public required int NodesDeleted { get; init; }
    /// <summary>Number of graph edges deleted.</summary>
    public required int EdgesDeleted { get; init; }
    /// <summary>Number of feedback weight entries deleted.</summary>
    public required int FeedbackWeightsDeleted { get; init; }
    /// <summary>Number of vector embeddings deleted.</summary>
    public required int VectorEmbeddingsDeleted { get; init; }
}
