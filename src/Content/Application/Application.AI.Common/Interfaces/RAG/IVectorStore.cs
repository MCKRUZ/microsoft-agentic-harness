using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Abstraction over vector similarity search backends. Implementations are registered
/// as keyed services by provider name (e.g., <c>"azure_ai_search"</c>, <c>"faiss"</c>,
/// <c>"qdrant"</c>) and selected via <c>AppConfig:AI:Rag:VectorStore:Provider</c>.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Index operations should be idempotent — re-indexing a chunk with the same
///         <see cref="DocumentChunk.Id"/> must upsert, not duplicate.</item>
///   <item>Search returns results ordered by descending similarity score (cosine, dot product,
///         or L2 depending on the backend). The score semantics are backend-specific but
///         normalized to [0, 1] in the returned <see cref="RetrievalResult.DenseScore"/>.</item>
///   <item>Collection names enable multi-tenant or multi-corpus isolation within a single
///         backend deployment. When null, use the default collection from configuration.</item>
///   <item>Delete operations remove all chunks for a given <paramref name="documentId"/>,
///         enabling clean re-ingestion workflows.</item>
///   <item>Implementations must handle connection pooling and dispose patterns appropriate
///         to the backend SDK.</item>
/// </list>
/// </remarks>
public interface IVectorStore
{
    /// <summary>
    /// Indexes (upserts) chunks into the vector store.
    /// </summary>
    /// <param name="chunks">Chunks with <see cref="DocumentChunk.Embedding"/> populated.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IndexAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string? collectionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a vector similarity search against indexed chunks.
    /// </summary>
    /// <param name="queryEmbedding">The query vector to search against.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results ordered by descending similarity score.</returns>
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all chunks belonging to the specified document.
    /// </summary>
    /// <param name="documentId">The document ID whose chunks should be removed.</param>
    /// <param name="collectionName">Optional collection/index name. Null uses the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(
        string documentId,
        string? collectionName = null,
        CancellationToken cancellationToken = default);
}
