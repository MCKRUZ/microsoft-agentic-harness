using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

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
    Task<SourceRetrievalResult> RetrieveAsync(
        string query,
        int topK,
        QueryComplexity complexity,
        CancellationToken cancellationToken);
}
