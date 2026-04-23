using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Expands retrieved chunks to include their full parent sections from the skeleton
/// tree (Proxy-Pointer RAG pattern). When a small chunk is retrieved, the expander
/// fetches the broader section it belongs to, providing richer context for the LLM
/// without the noise of full-document retrieval. Deduplicates overlapping sections
/// when multiple chunks share a parent.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Use the <see cref="DocumentChunk.SectionPath"/> breadcrumb to locate the chunk's
///         position in the skeleton tree, then retrieve the parent section's full content.</item>
///   <item>Expansion depth is configurable via <c>AppConfig:AI:Rag:PointerExpansionDepth</c>
///         (default: 1 level up). Deeper expansion provides more context but consumes more
///         token budget.</item>
///   <item>Deduplicate expanded sections: if two chunks share the same parent section,
///         include the parent only once. Use section path string comparison for dedup.</item>
///   <item>Expanded chunks should be marked in metadata so downstream consumers can
///         distinguish between directly-retrieved and expansion-added content.</item>
///   <item>If the skeleton tree is not available for a chunk (e.g., from a non-structured
///         document), return the chunk unchanged — expansion is best-effort.</item>
/// </list>
/// </remarks>
public interface IPointerExpander
{
    /// <summary>
    /// Expands chunks to include broader parent sections from the document structure.
    /// </summary>
    /// <param name="chunks">The retrieved chunks to expand.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An expanded and deduplicated list of chunks. May be larger than the input if
    /// parent sections were added, or the same size if no expansion was possible.
    /// </returns>
    Task<IReadOnlyList<DocumentChunk>> ExpandAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);
}
