namespace Domain.AI.RAG.Models;

/// <summary>
/// Aggregate result of multi-hop iterative retrieval.
/// </summary>
public sealed record IterativeRetrievalResult
{
    /// <summary>Ordered list of hop results from each iteration.</summary>
    public required IReadOnlyList<HopResult> Hops { get; init; }

    /// <summary>All results aggregated across hops, deduplicated by chunk ID.</summary>
    public required IReadOnlyList<RetrievalResult> AggregatedResults { get; init; }

    /// <summary>Total token count consumed across all hops.</summary>
    public required int TotalTokensUsed { get; init; }

    /// <summary>Whether the token budget was exhausted before completion.</summary>
    public required bool BudgetExhausted { get; init; }
}
