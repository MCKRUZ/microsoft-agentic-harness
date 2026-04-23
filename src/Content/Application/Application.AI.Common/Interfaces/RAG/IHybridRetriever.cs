using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Combines dense (vector) and sparse (BM25) retrieval with Reciprocal Rank Fusion
/// (RRF) to produce a single ranked list of results. Hybrid retrieval captures both
/// semantic similarity (via embeddings) and lexical precision (via BM25), consistently
/// outperforming either approach alone.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Run <see cref="IVectorStore.SearchAsync"/> and <see cref="IBm25Store.SearchAsync"/>
///         concurrently via <c>Task.WhenAll</c> for optimal latency.</item>
///   <item>Apply RRF formula: <c>score = sum(1 / (k + rank_i))</c> where <c>k</c> is typically
///         60 (configurable via <c>AppConfig:AI:Rag:RrfK</c>).</item>
///   <item>Request more candidates from each store than <paramref name="topK"/> (typically 2-3x)
///         to ensure sufficient overlap for meaningful fusion.</item>
///   <item>Populate both <see cref="RetrievalResult.DenseScore"/> and
///         <see cref="RetrievalResult.SparseScore"/> on each fused result for observability.</item>
///   <item>When one store is unavailable, fall back to single-source retrieval rather than
///         failing — degrade gracefully.</item>
/// </list>
/// </remarks>
public interface IHybridRetriever
{
    /// <summary>
    /// Performs hybrid retrieval combining dense and sparse search with rank fusion.
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="topK">Maximum number of fused results to return.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results ordered by descending fused score.</returns>
    Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
