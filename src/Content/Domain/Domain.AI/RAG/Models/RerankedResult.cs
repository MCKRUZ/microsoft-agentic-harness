namespace Domain.AI.RAG.Models;

/// <summary>
/// A retrieval result that has been re-scored by a cross-encoder reranking model.
/// Reranking applies a more expensive but more accurate relevance assessment than
/// initial retrieval, typically processing the top-N results from the retrieval phase.
/// Tracks both the original and reranked positions to measure reranker impact.
/// </summary>
public record RerankedResult
{
    /// <summary>
    /// The original retrieval result with its dense, sparse, and fused scores.
    /// Preserved to enable comparison between retrieval and reranking stages.
    /// </summary>
    public required RetrievalResult RetrievalResult { get; init; }

    /// <summary>
    /// The relevance score assigned by the cross-encoder reranking model (0.0 to 1.0).
    /// Unlike retrieval scores which compare embeddings independently, the rerank score
    /// is computed from the full (query, chunk) pair jointly, yielding higher accuracy.
    /// </summary>
    public required double RerankScore { get; init; }

    /// <summary>
    /// The 1-based position of this result in the original retrieval ranking
    /// (sorted by <see cref="RetrievalResult.FusedScore"/>). Used to measure
    /// how much the reranker reordered results.
    /// </summary>
    public required int OriginalRank { get; init; }

    /// <summary>
    /// The 1-based position of this result after reranking (sorted by
    /// <see cref="RerankScore"/>). Comparing with <see cref="OriginalRank"/>
    /// reveals which chunks were promoted or demoted by the reranker.
    /// </summary>
    public required int RerankRank { get; init; }
}
