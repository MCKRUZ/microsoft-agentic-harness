using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Blends reranked retrieval scores with historical feedback weights from the
/// knowledge graph. Inserted between the reranking and CRAG evaluation stages
/// so that CRAG evaluates feedback-adjusted rankings.
/// </summary>
/// <remarks>
/// Score blending formula:
/// <c>adjustedScore = (1 - alpha) * rerankScore + alpha * avgNodeWeight</c>,
/// where <c>alpha</c> is <c>GraphRagConfig.FeedbackAlpha</c> and
/// <c>avgNodeWeight</c> is the average feedback weight of graph nodes
/// referenced by the chunk's entity matches.
/// </remarks>
public interface IFeedbackWeightedScorer
{
    /// <summary>
    /// Adjusts reranked results by blending in feedback weights, then re-sorts
    /// by adjusted score. Results without graph entity matches pass through unchanged.
    /// </summary>
    /// <param name="rerankedResults">The results from the reranking stage.</param>
    /// <param name="query">The original search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RerankedResult>> BlendFeedbackAsync(
        IReadOnlyList<RerankedResult> rerankedResults,
        string query,
        CancellationToken cancellationToken = default);
}
