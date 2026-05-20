using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Orchestrates multi-hop iterative retrieval for complex queries.
/// </summary>
public interface IIterativeRetriever
{
    /// <summary>Execute iterative multi-hop retrieval for a complex query.</summary>
    Task<IterativeRetrievalResult> RetrieveIterativelyAsync(
        string query,
        int topKPerHop,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
