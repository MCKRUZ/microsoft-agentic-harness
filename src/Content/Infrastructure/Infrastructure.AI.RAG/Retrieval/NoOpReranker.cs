using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Identity passthrough implementation of <see cref="IReranker"/> that preserves
/// the original retrieval ordering. Wraps each <see cref="RetrievalResult"/> in a
/// <see cref="RerankedResult"/> using the fused score as the rerank score.
/// Registered as keyed service <c>"none"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Use this reranker when:
/// <list type="bullet">
///   <item>Reranking is disabled in configuration (<c>Reranker:Strategy = "none"</c>).</item>
///   <item>Benchmarking retrieval quality without reranking overhead.</item>
///   <item>The retrieval pipeline already produces sufficiently accurate ordering.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class NoOpReranker : IReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RerankedResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var reranked = results
            .Take(topK)
            .Select((r, i) => new RerankedResult
            {
                RetrievalResult = r,
                RerankScore = r.FusedScore,
                OriginalRank = i + 1,
                RerankRank = i + 1,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RerankedResult>>(reranked);
    }
}
