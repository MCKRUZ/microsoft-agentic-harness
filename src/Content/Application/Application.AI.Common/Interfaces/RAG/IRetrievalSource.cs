using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Pluggable retrieval source resolved via keyed DI.
/// Each implementation is registered with a string key (e.g., "vector", "graph", "web_search", "sql_database").
/// The <see cref="IMultiSourceOrchestrator"/> resolves enabled sources by key and fans out retrieval in parallel.
/// </summary>
public interface IRetrievalSource
{
    /// <summary>
    /// Unique identifier for this source, matching the keyed DI registration key.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Executes retrieval against this source and returns results with per-source latency and token metrics.
    /// </summary>
    /// <param name="query">The natural-language query to retrieve for.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="complexity">The classified query complexity driving source selection.</param>
    /// <param name="collectionName">
    /// Optional collection/index name. Null uses the source's default. Sources without a
    /// collection concept (graph, web search, SQL) accept and ignore it — such sources are
    /// tenant-shared even when <c>ScopedCollections</c> is enabled, as documented on
    /// <c>ScopedCollectionsConfig</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SourceRetrievalResult> RetrieveAsync(
        string query,
        int topK,
        TaskComplexity complexity,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
