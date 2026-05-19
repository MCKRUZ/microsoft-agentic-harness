namespace Domain.AI.RAG.Models;

/// <summary>
/// The result of decomposing a complex query into ordered sub-queries.
/// </summary>
public sealed record DecomposedQuery
{
    /// <summary>The original user query before decomposition.</summary>
    public required string OriginalQuery { get; init; }

    /// <summary>The ordered list of sub-queries. Guaranteed at least one.</summary>
    public required IReadOnlyList<SubQuery> SubQueries { get; init; }

    /// <summary>Whether any sub-query has dependencies requiring sequential execution.</summary>
    public bool RequiresSequentialExecution =>
        SubQueries.Any(sq => sq.DependsOnOrders.Count > 0);
}
