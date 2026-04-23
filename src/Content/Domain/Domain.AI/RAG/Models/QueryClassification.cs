using Domain.AI.RAG.Enums;

namespace Domain.AI.RAG.Models;

/// <summary>
/// The result of classifying a user query to determine its type and the optimal
/// retrieval strategy. Classification runs before retrieval so the pipeline can
/// select the right strategy (e.g., graph traversal for multi-hop, broad retrieval
/// for thematic queries) rather than applying a one-size-fits-all approach.
/// </summary>
public record QueryClassification
{
    /// <summary>
    /// The classified type of the query, indicating its complexity and
    /// the reasoning pattern needed to answer it.
    /// </summary>
    public required QueryType Type { get; init; }

    /// <summary>
    /// The retrieval strategy selected based on the query type.
    /// The classifier maps query types to strategies, but the mapping is
    /// configurable — a simple lookup doesn't always need hybrid search.
    /// </summary>
    public required RetrievalStrategy Strategy { get; init; }

    /// <summary>
    /// The classifier's confidence in its classification (0.0 to 1.0).
    /// Below a configurable threshold (e.g., 0.7), the pipeline may fall back
    /// to the default <see cref="RetrievalStrategy.HybridVectorBm25"/> strategy.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional chain-of-thought reasoning from the classifier explaining why
    /// this query type and strategy were selected. Useful for debugging and
    /// observability but not consumed by downstream pipeline stages.
    /// </summary>
    public string? Reasoning { get; init; }
}
