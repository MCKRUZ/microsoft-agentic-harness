using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Adapts <see cref="IHybridRetriever"/> to the <see cref="IRetrievalSource"/> contract.
/// Registered as keyed DI with key "vector".
/// </summary>
internal sealed class VectorRetrievalSource(IHybridRetriever hybridRetriever) : IRetrievalSource
{
    /// <inheritdoc />
    public string SourceName => "vector";

    /// <inheritdoc />
    public async Task<SourceRetrievalResult> RetrieveAsync(
        string query,
        int topK,
        TaskComplexity complexity,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var results = await hybridRetriever.RetrieveAsync(
            query, topK, collectionName, cancellationToken);
        sw.Stop();

        return new SourceRetrievalResult
        {
            SourceName = SourceName,
            Results = results,
            Latency = sw.Elapsed,
            TokensUsed = 0
        };
    }
}
