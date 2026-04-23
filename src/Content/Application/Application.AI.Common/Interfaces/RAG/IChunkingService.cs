using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Splits document content into chunks suitable for embedding and retrieval.
/// Implementations are registered as keyed services by strategy name:
/// <c>"structure_aware"</c>, <c>"semantic"</c>, <c>"fixed_size"</c>.
/// The active strategy is selected via <c>AppConfig:AI:Rag:ChunkingStrategy</c>.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item><c>"structure_aware"</c> (default): Uses <paramref name="structure"/> to split at
///         heading boundaries, respecting the document's natural organization. Preferred for
///         structured documents (specs, docs, reports).</item>
///   <item><c>"semantic"</c>: Uses embedding similarity between sentences to detect topic
///         shifts. Better for unstructured prose (transcripts, logs).</item>
///   <item><c>"fixed_size"</c>: Simple token-window chunking with configurable overlap.
///         Fastest but lowest quality.</item>
///   <item>All strategies must populate <see cref="DocumentChunk.SectionPath"/> using the
///         skeleton tree's breadcrumb path, even when the chunking boundary is token-based.</item>
///   <item>Chunk token counts must be measured with the tokenizer aligned to the configured
///         embedding model.</item>
/// </list>
/// </remarks>
public interface IChunkingService
{
    /// <summary>
    /// Chunks the document content using the skeleton structure for boundary awareness.
    /// </summary>
    /// <param name="content">The full markdown content to chunk.</param>
    /// <param name="structure">
    /// The skeleton tree from <see cref="IStructureExtractor"/>. Used for boundary detection
    /// and breadcrumb path generation regardless of chunking strategy.
    /// </param>
    /// <param name="sourceUri">The URI of the source document for provenance metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An ordered list of chunks preserving document order.</returns>
    Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string content,
        SkeletonNode structure,
        Uri sourceUri,
        CancellationToken cancellationToken = default);
}
