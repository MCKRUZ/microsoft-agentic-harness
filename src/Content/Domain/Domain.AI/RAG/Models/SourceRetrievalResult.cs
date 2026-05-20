namespace Domain.AI.RAG.Models;

/// <summary>
/// Wraps retrieval results from a single source (vector, graph, web) with
/// per-source performance metrics.
/// </summary>
public sealed record SourceRetrievalResult
{
    /// <summary>Identifies the retrieval source (e.g., "vector", "graph", "web").</summary>
    public required string SourceName { get; init; }

    /// <summary>Results returned by this source.</summary>
    public required IReadOnlyList<RetrievalResult> Results { get; init; }

    /// <summary>Wall-clock time this source took to respond.</summary>
    public required TimeSpan Latency { get; init; }

    /// <summary>Tokens consumed by this source's retrieval operations.</summary>
    public required int TokensUsed { get; init; }
}
