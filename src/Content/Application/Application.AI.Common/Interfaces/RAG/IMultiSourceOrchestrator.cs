using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Coordinates retrieval across multiple sources (vector store, knowledge graph, web)
/// in parallel, merges results, and deduplicates by chunk ID. Source selection is
/// driven by query complexity.
/// </summary>
public interface IMultiSourceOrchestrator
{
    /// <summary>
    /// Retrieves results from all applicable sources based on query complexity,
    /// merges, deduplicates, and returns a unified result list sorted by fused score.
    /// </summary>
    /// <param name="query">The natural-language query to retrieve for.</param>
    /// <param name="topK">Maximum number of results to return after merging.</param>
    /// <param name="complexity">The classified query complexity driving source selection.</param>
    /// <param name="collectionName">
    /// Optional collection/index name forwarded to each source. Null uses each source's
    /// default. Only collection-aware sources (the <c>"vector"</c> source) honor it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RetrievalResult>> RetrieveFromAllSourcesAsync(
        string query,
        int topK,
        TaskComplexity complexity,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
