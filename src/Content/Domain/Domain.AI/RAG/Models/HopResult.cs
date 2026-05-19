namespace Domain.AI.RAG.Models;

/// <summary>
/// The result of a single retrieval hop within the iterative retrieval loop.
/// </summary>
public sealed record HopResult
{
    /// <summary>The sub-query used for retrieval in this hop.</summary>
    public required SubQuery SubQuery { get; init; }

    /// <summary>The retrieval results obtained for this hop's sub-query.</summary>
    public required IReadOnlyList<RetrievalResult> Results { get; init; }

    /// <summary>Sufficiency score (0.0–1.0) for this hop's context.</summary>
    public required double SufficiencyScore { get; init; }

    /// <summary>The 1-based hop number within the iterative sequence.</summary>
    public required int HopNumber { get; init; }

    /// <summary>Whether the context was deemed sufficient.</summary>
    public required bool IsSufficient { get; init; }
}
