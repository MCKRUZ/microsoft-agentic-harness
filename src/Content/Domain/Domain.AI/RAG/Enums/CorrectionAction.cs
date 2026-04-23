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
    Reject
}
