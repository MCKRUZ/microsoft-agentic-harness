using Domain.AI.RAG.Enums;

namespace Domain.AI.Planner;

/// <summary>
/// Configuration for a retrieval plan step. Specifies the query, retrieval strategy,
/// result count, and whether to use multi-source orchestration across vector, graph,
/// and web sources.
/// </summary>
public sealed record RetrievalStepConfiguration : StepConfiguration
{
    /// <summary>
    /// The retrieval query text. May contain upstream output placeholders that are
    /// resolved by the executor before retrieval.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional retrieval strategy override. When null, the query classifier determines
    /// the strategy based on query complexity.
    /// </summary>
    public RetrievalStrategy? Strategy { get; init; }

    /// <summary>
    /// Maximum number of results to return. When null, uses the default from
    /// <c>AppConfig:AI:Rag:Retrieval:TopK</c>.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Optional collection or index name to search. When null, the default collection is used.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// When <c>true</c>, uses <see cref="IMultiSourceOrchestrator"/> to fan out across
    /// vector, graph, and web sources in parallel. When <c>false</c>, uses the standard
    /// <see cref="IRagOrchestrator"/> single-pipeline path.
    /// </summary>
    public bool UseMultiSource { get; init; } = false;
}
