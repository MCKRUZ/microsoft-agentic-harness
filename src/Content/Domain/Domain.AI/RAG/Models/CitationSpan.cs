namespace Domain.AI.RAG.Models;

/// <summary>
/// Identifies the exact source location within a document that supports a claim
/// in the generated response. Used to render inline citations and enable
/// "click to verify" UX patterns where users can jump to the original source text.
/// </summary>
public record CitationSpan
{
    /// <summary>
    /// The ID of the <see cref="DocumentChunk"/> that contains the cited content.
    /// Links back to the chunk for full context retrieval.
    /// </summary>
    public required string ChunkId { get; init; }

    /// <summary>
    /// The URI of the original source document. Enables direct linking to the
    /// source even when the chunk store is not accessible to the end user.
    /// </summary>
    public required Uri DocumentUri { get; init; }

    /// <summary>
    /// The breadcrumb path within the document (e.g., "10-K > Risk Factors > Market Risk").
    /// Displayed in citation UI to give users structural context without opening the document.
    /// </summary>
    public required string SectionPath { get; init; }

    /// <summary>
    /// The character offset within the chunk content where the cited span begins.
    /// Enables highlighting the exact supporting text within a chunk.
    /// </summary>
    public required int StartOffset { get; init; }

    /// <summary>
    /// The character offset within the chunk content where the cited span ends.
    /// The range [<see cref="StartOffset"/>, <see cref="EndOffset"/>) defines
    /// the exact cited text.
    /// </summary>
    public required int EndOffset { get; init; }
}
