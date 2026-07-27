using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Adapts <see cref="IGraphRagService"/> to the <see cref="IRetrievalSource"/> contract.
/// Uses <see cref="IGraphRagService.LocalSearchAsync"/> for entity-neighborhood retrieval.
/// Registered as keyed DI with key "graph".
/// </summary>
internal sealed class GraphRetrievalSource(IGraphRagService graphRagService) : IRetrievalSource
{
    /// <inheritdoc />
    public string SourceName => "graph";

    /// <inheritdoc />
    /// <remarks>
    /// The corpus graph is a single shared graph with no collection concept, so
    /// <paramref name="collectionName"/> is accepted and ignored: graph retrieval stays
    /// tenant-shared even when <c>ScopedCollections</c> is enabled (documented on
    /// <c>ScopedCollectionsConfig</c>).
    /// </remarks>
    public async Task<SourceRetrievalResult> RetrieveAsync(
        string query,
        int topK,
        TaskComplexity complexity,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var results = await graphRagService.LocalSearchAsync(query, topK, cancellationToken);
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
