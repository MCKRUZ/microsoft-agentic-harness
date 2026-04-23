namespace Domain.AI.RAG.Enums;

/// <summary>
/// Determines how a document is split into chunks for embedding and retrieval.
/// The choice of strategy directly impacts retrieval quality — structure-aware chunking
/// preserves semantic boundaries (headings, paragraphs) while fixed-size chunking
/// guarantees uniform token counts at the cost of splitting mid-thought.
/// </summary>
public enum ChunkingStrategy
{
    /// <summary>
    /// Splits on document structure boundaries (headings, sections, paragraphs).
    /// Preserves semantic coherence by respecting the author's organizational intent.
    /// Best for well-structured documents like specifications, reports, and documentation.
    /// </summary>
    StructureAware,

    /// <summary>
    /// Uses embedding similarity to detect topic shifts and split at natural boundaries.
    /// More expensive than structure-aware (requires a pre-pass embedding), but works well
    /// on unstructured text like transcripts and emails where headings are absent.
    /// </summary>
    Semantic,

    /// <summary>
    /// Splits at fixed token counts with configurable overlap.
    /// Simple and predictable, but may break mid-sentence or mid-paragraph.
    /// Use as a fallback when document structure is unreliable.
    /// </summary>
    FixedSize,

    /// <summary>
    /// Builds a tree of chunks at multiple granularity levels (sentence, paragraph, section).
    /// Enables RAPTOR-style retrieval where the system can match at the appropriate abstraction level.
    /// Highest storage cost but best retrieval flexibility.
    /// </summary>
    Hierarchical
}
