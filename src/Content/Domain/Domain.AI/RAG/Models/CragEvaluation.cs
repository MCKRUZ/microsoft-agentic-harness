using Domain.AI.RAG.Enums;

namespace Domain.AI.RAG.Models;

/// <summary>
/// The result of a CRAG (Corrective Retrieval-Augmented Generation) evaluation
/// that assesses whether retrieved chunks are relevant enough to produce a faithful
/// answer. If relevance is low, the evaluation prescribes a corrective action —
/// refining the query, falling back to web search, or rejecting the retrieval entirely.
/// </summary>
public record CragEvaluation
{
    /// <summary>
    /// The corrective action to take based on the relevance assessment.
    /// Drives the pipeline's decision to proceed, retry, fallback, or refuse.
    /// </summary>
    public required CorrectionAction Action { get; init; }

    /// <summary>
    /// An aggregate relevance score (0.0 to 1.0) across all evaluated chunks.
    /// Computed by the CRAG evaluator (typically a lightweight LLM or cross-encoder).
    /// Below the configured threshold, the pipeline triggers the prescribed
    /// <see cref="Action"/> instead of proceeding to generation.
    /// </summary>
    public required double RelevanceScore { get; init; }

    /// <summary>
    /// Optional reasoning from the evaluator explaining why the retrieval was
    /// assessed at this relevance level. Logged for observability and debugging.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// IDs of chunks that scored below the individual relevance threshold.
    /// These chunks are candidates for removal before context assembly to avoid
    /// injecting misleading or off-topic content into the generation prompt.
    /// </summary>
    public IReadOnlyList<string> WeakChunkIds { get; init; } = [];
}
