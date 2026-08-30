namespace Domain.AI.RAG.Enums;

/// <summary>
/// Defines the corrective action to take after CRAG (Corrective RAG) evaluates
/// the relevance of retrieved chunks. Determines whether to use results as-is,
/// refine the query, fall back to web search, or reject the retrieval entirely.
/// </summary>
public enum CorrectionAction
{
    /// <summary>
    /// Retrieved chunks are sufficiently relevant. Proceed with generation
    /// using the current retrieval results without modification.
    /// </summary>
    Accept,

    /// <summary>
    /// Retrieved chunks are partially relevant. Refine the query and perform
    /// a second retrieval pass, potentially with a different strategy.
    /// </summary>
    Refine,

    /// <summary>
    /// Retrieved chunks are not relevant and the corpus likely lacks the answer.
    /// Fall back to web search or an external knowledge source.
    /// </summary>
    WebFallback,

    /// <summary>
    /// Retrieved chunks are irrelevant or misleading. Reject the retrieval
    /// entirely and respond with an explicit "I don't have this information"
    /// rather than hallucinating from poor context.
    /// </summary>
    Reject,

    /// <summary>
    /// The CRAG gate itself did not run — the evaluation call threw, or the model's response could
    /// not be parsed even after a repair attempt. This is <em>not</em> a relevance judgment and
    /// must never be treated as equivalent to <see cref="Accept"/>: a gate that cannot read its own
    /// output must not silently green-light the retrieval it exists to check. Every consumer of
    /// <c>CragEvaluation.Action</c> must handle this explicitly rather than falling through to
    /// whatever the default branch happens to do for an unrecognised value.
    /// </summary>
    EvaluationUnavailable,
}
