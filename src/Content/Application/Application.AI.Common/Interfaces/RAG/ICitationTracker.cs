using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Tracks citation spans during RAG context assembly, mapping regions of the
/// assembled text back to their source chunks and document locations. Consumed
/// by <see cref="IRagContextAssembler"/> during assembly and by UI layers to
/// render inline citations and "click to verify" source attribution.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Register as <c>Transient</c> — each assembly operation gets a fresh tracker
///         instance. Do not share across concurrent assemblies.</item>
///   <item>Track is called by the assembler as it appends each chunk's text to the output
///         buffer. The <paramref name="assembledOffset"/> and <paramref name="length"/>
///         describe the span within the assembled string.</item>
///   <item><see cref="GetCitations"/> should return spans sorted by offset ascending
///         so consumers can walk citations in document order.</item>
///   <item>Detect and merge adjacent spans from the same chunk (can occur after pointer
///         expansion) to avoid redundant citations.</item>
///   <item><see cref="Reset"/> clears all tracked spans, enabling reuse of the tracker
///         instance within a single assembly pipeline run.</item>
/// </list>
/// </remarks>
public interface ICitationTracker
{
    /// <summary>
    /// Records a citation span linking a region of the assembled context to its source chunk.
    /// </summary>
    /// <param name="chunk">The source chunk whose content occupies this span.</param>
    /// <param name="assembledOffset">
    /// Zero-based character offset within the assembled context string where this
    /// chunk's content begins.
    /// </param>
    /// <param name="length">Length in characters of the chunk's content in the assembled string.</param>
    void Track(DocumentChunk chunk, int assembledOffset, int length);

    /// <summary>
    /// Returns all tracked citation spans, sorted by assembled offset ascending.
    /// </summary>
    /// <returns>An immutable list of citation spans.</returns>
    IReadOnlyList<CitationSpan> GetCitations();

    /// <summary>
    /// Clears all tracked citation spans.
    /// </summary>
    void Reset();
}
