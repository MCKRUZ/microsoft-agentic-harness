using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Reranks retrieval results using a cross-encoder or semantic reranking model.
/// Cross-encoders process (query, document) pairs jointly, producing more accurate
/// relevance scores than the independent embeddings used in initial retrieval.
/// Implementations are registered as keyed services: <c>"azure_semantic"</c>,
/// <c>"cross_encoder"</c>, <c>"none"</c> (passthrough).
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item><c>"azure_semantic"</c>: Uses Azure AI Search's built-in semantic ranker.
///         Simplest option when Azure AI Search is the vector store.</item>
///   <item><c>"cross_encoder"</c>: Uses a local or API-hosted cross-encoder model
///         (e.g., ms-marco-MiniLM). More control but requires model hosting.</item>
///   <item><c>"none"</c>: Passthrough that converts <see cref="RetrievalResult"/> to
///         <see cref="RerankedResult"/> using the fused score as the rerank score.
///         Used when reranking is disabled or for benchmarking.</item>
///   <item>Reranking is expensive — only process the top N results from retrieval
///         (configured via <c>AppConfig:AI:Rag:RerankCandidates</c>, typically 20-50).</item>
///   <item>Return exactly <paramref name="topK"/> results (or fewer if input is smaller),
///         sorted by descending rerank score.</item>
/// </list>
/// </remarks>
public interface IReranker
{
    /// <summary>
    /// Reranks retrieval results for relevance to the query.
    /// </summary>
    /// <param name="query">The original user query.</param>
    /// <param name="results">The retrieval results to rerank.</param>
    /// <param name="topK">Maximum number of reranked results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results reranked by cross-encoder relevance, descending.</returns>
    Task<IReadOnlyList<RerankedResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        int topK,
        CancellationToken cancellationToken = default);
}
