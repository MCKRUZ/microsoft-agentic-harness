using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Decomposes complex queries into ordered sub-queries with dependency tracking.
/// </summary>
public interface IQueryDecomposer
{
    /// <summary>Decompose a complex query into ordered sub-queries.</summary>
    Task<DecomposedQuery> DecomposeAsync(string query, CancellationToken cancellationToken = default);
}
