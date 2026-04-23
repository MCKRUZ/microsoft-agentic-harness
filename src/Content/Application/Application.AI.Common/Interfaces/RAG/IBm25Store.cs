using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Abstraction over BM25/full-text search backends for sparse retrieval.
/// Used alongside <see cref="IVectorStore"/> for hybrid search with Reciprocal Rank
/// Fusion (RRF). BM25 excels at exact keyword matching and technical terminology
/// that embedding models may not capture well.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Azure AI Search: Use the built-in full-text scoring profile alongside vector
///         search — a single backend can serve both <see cref="IVectorStore"/> and
///         <see cref="IBm25Store"/>.</item>
///   <item>Standalone: Use Lucene.NET or Elasticsearch for BM25 scoring when the vector
///         store backend does not support full-text search natively.</item>
///   <item>Index the raw chunk text (not the contextual prefix) for BM25. The prefix
///         improves embedding similarity but adds noise to keyword matching.</item>
///   <item>Sparse scores in <see cref="RetrievalResult.SparseScore"/> should be normalized
///         to [0, 1] for consistent fusion with dense scores.</item>
///   <item>Collection semantics mirror <see cref="IVectorStore"/>: null uses the default,
///         named collections enable multi-corpus isolation.</item>
/// </list>
/// </remarks>
public interface IBm25Store
{
    /// <summary>
    /// Indexes chunks for BM25 full-text search.
    /// </summary>
    /// <param name="chunks">The chunks to index.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IndexAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string? collectionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a BM25 keyword search against indexed chunks.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results ordered by descending BM25 score.</returns>
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all chunks belonging to the specified document from the BM25 index.
    /// </summary>
    /// <param name="documentId">The document ID whose chunks should be removed.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(
        string documentId,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
