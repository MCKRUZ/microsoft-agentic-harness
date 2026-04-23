using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Generates vector embeddings for document chunks and queries. Wraps
/// <c>IEmbeddingGenerator</c> from <c>Microsoft.Extensions.AI</c> with batching,
/// retry policies, and OpenTelemetry instrumentation. Ensures consistent embedding
/// model usage across ingestion and retrieval paths.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Use the embedding model configured in <c>AppConfig:AI:Rag:EmbeddingModel</c>.
///         The model determines embedding dimensionality (e.g., 1536 for ada-002,
///         3072 for text-embedding-3-large).</item>
///   <item>Batch embedding requests to the configured batch size (typically 16-64 chunks)
///         to maximize throughput while respecting API rate limits.</item>
///   <item>Apply retry policies (exponential backoff) for transient failures. Individual
///         chunk failures should not fail the entire batch — log and skip.</item>
///   <item>Prepend <see cref="ChunkMetadata.ContextualPrefix"/> to chunk content before
///         embedding if contextual enrichment was applied.</item>
///   <item>Emit OpenTelemetry metrics: embedding latency, batch size, token usage,
///         and failure count.</item>
/// </list>
/// </remarks>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates embeddings for a batch of document chunks.
    /// </summary>
    /// <param name="chunks">The chunks to embed. Returned chunks will have
    /// <see cref="DocumentChunk.Embedding"/> populated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A new list of chunks with embeddings populated. Chunks that failed embedding
    /// are excluded from the result and logged as warnings.
    /// </returns>
    Task<IReadOnlyList<DocumentChunk>> EmbedAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an embedding for a single query string, suitable for vector similarity
    /// search against indexed chunk embeddings.
    /// </summary>
    /// <param name="query">The query text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query embedding vector.</returns>
    Task<ReadOnlyMemory<float>> EmbedQueryAsync(
        string query,
        CancellationToken cancellationToken = default);
}
