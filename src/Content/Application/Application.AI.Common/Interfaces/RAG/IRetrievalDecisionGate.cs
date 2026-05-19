using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Decides the retrieval execution path based on query complexity.
/// Returns the effective retrieval parameters for the classified tier.
/// </summary>
public interface IRetrievalDecisionGate
{
    /// <summary>
    /// Determine retrieval parameters based on complexity classification.
    /// </summary>
    /// <param name="classification">The complexity classification result.</param>
    /// <param name="requestedTopK">Optional topK override from the caller.</param>
    /// <returns>Effective retrieval parameters for this query.</returns>
    RetrievalDecision Decide(ComplexityClassification classification, int? requestedTopK = null);
}

/// <summary>
/// The retrieval execution decision — what pipeline path to take and with what parameters.
/// </summary>
public sealed record RetrievalDecision
{
    /// <summary>Whether to skip retrieval entirely and let the LLM answer from parametric knowledge.</summary>
    public required bool SkipRetrieval { get; init; }

    /// <summary>Effective topK for this query.</summary>
    public required int TopK { get; init; }

    /// <summary>Whether to run the reranker.</summary>
    public required bool UseReranking { get; init; }

    /// <summary>Whether to run CRAG evaluation.</summary>
    public required bool UseCragEvaluation { get; init; }

    /// <summary>The complexity tier that produced this decision.</summary>
    public required QueryComplexity Complexity { get; init; }

    /// <summary>Optional strategy override based on complexity.</summary>
    public RetrievalStrategy? StrategyOverride { get; init; }
}
