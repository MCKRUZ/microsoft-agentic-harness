using Domain.AI.RAG.Enums;

namespace Domain.AI.RAG.Models;

/// <summary>
/// Result of query complexity classification with confidence and reasoning.
/// </summary>
public sealed record ComplexityClassification
{
    public required QueryComplexity Complexity { get; init; }
    public required double Confidence { get; init; }
    public string? Reasoning { get; init; }

    /// <summary>Whether retrieval should be skipped entirely for this query.</summary>
    public bool SkipRetrieval => Complexity == QueryComplexity.Trivial;
}
