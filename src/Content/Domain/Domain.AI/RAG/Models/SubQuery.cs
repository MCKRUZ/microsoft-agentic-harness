namespace Domain.AI.RAG.Models;

/// <summary>
/// A single sub-query produced by decomposing a complex query into smaller,
/// independently-retrievable parts. Each sub-query has an execution order
/// and may declare dependencies on other sub-queries whose results must be
/// available before this sub-query can be meaningfully answered.
/// </summary>
public sealed record SubQuery
{
    /// <summary>The natural language text of this sub-query.</summary>
    public required string Text { get; init; }

    /// <summary>The 1-based execution order within the decomposition.</summary>
    public required int Order { get; init; }

    /// <summary>Orders of sub-queries that must complete before this one executes.</summary>
    public IReadOnlyList<int> DependsOnOrders { get; init; } = [];
}
