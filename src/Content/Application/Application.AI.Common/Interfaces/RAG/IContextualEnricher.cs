using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Generates contextual prefixes for chunks using an LLM (Anthropic Contextual Retrieval
/// pattern). Each chunk receives a 50-100 token summary situating it within the broader
/// document, improving retrieval accuracy by up to 49% (per Anthropic's benchmarks).
/// The prefix is stored in <see cref="ChunkMetadata.ContextualPrefix"/> and prepended
/// to chunk content at embedding and retrieval time.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Use <see cref="IRagModelRouter"/> to select an economy-tier model — this is a
///         high-volume, low-complexity LLM call (one per chunk).</item>
///   <item>Batch chunks to maximize throughput; respect rate limits via retry policies.</item>
///   <item>The prompt template should include the full document text (or a large window around
///         the chunk) so the LLM can describe the chunk's context accurately.</item>
///   <item>If the LLM call fails for a chunk, preserve the chunk without enrichment rather
///         than failing the entire batch — enrichment is an optimization, not a requirement.</item>
///   <item>Emit OpenTelemetry metrics for enrichment latency, token usage, and failure rate.</item>
/// </list>
/// </remarks>
public interface IContextualEnricher
{
    /// <summary>
    /// Enriches each chunk with a contextual prefix derived from the full document.
    /// </summary>
    /// <param name="chunks">The chunks to enrich.</param>
    /// <param name="fullDocumentContent">
    /// The full markdown text of the source document. Provided to the LLM as context
    /// so it can describe where each chunk sits within the document.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A new list of chunks with <see cref="ChunkMetadata.ContextualPrefix"/> populated.
    /// Chunks are returned in the same order as the input; failed enrichments retain
    /// a null prefix.
    /// </returns>
    Task<IReadOnlyList<DocumentChunk>> EnrichAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string fullDocumentContent,
        CancellationToken cancellationToken = default);
}
