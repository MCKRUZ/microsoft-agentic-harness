namespace Domain.AI.RAG.Models;

/// <summary>
/// The final assembled context ready to be injected into the LLM prompt for
/// grounded generation. Contains the concatenated chunk text, citation metadata
/// for attribution, and token accounting for context budget management.
/// This is the output of the RAG pipeline's assembly phase and the input to generation.
/// </summary>
public record RagAssembledContext
{
    /// <summary>
    /// The concatenated and formatted text from all selected chunks, ready for
    /// injection into the system or user prompt. Chunks are ordered by relevance
    /// and separated by configurable delimiters.
    /// </summary>
    public required string AssembledText { get; init; }

    /// <summary>
    /// Citation spans linking regions of the assembled text back to their source
    /// chunks and documents. Used by the generation phase to produce inline citations
    /// and by the UI to render source attribution.
    /// </summary>
    public IReadOnlyList<CitationSpan> Citations { get; init; } = [];

    /// <summary>
    /// The total token count of <see cref="AssembledText"/> as measured by the
    /// tokenizer aligned with the generation model. Used by the context budget
    /// manager to determine how much space remains for conversation history
    /// and system instructions.
    /// </summary>
    public required int TotalTokens { get; init; }

    /// <summary>
    /// Indicates whether the assembled context was truncated to fit within the
    /// context budget. When true, lower-ranked chunks were dropped, and the
    /// generation model should be informed that its context is incomplete.
    /// </summary>
    public required bool WasTruncated { get; init; }
}
