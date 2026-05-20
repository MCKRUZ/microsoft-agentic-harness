using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

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
    public async Task<SourceRetrievalResult> RetrieveAsync(
        string query, int topK, QueryComplexity complexity, CancellationToken cancellationToken)
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
