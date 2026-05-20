namespace Domain.AI.RAG.Models;

/// <summary>
/// Ragas-inspired quality metrics for a single retrieval+generation cycle.
/// Produced by <c>IRetrievalQualityEvaluator</c> and used by CI/CD quality gates
/// to prevent quality regression.
/// </summary>
public sealed record RetrievalQualityReport
{
    /// <summary>
    /// Fraction of retrieved context chunks that are relevant to the query (0.0-1.0).
    /// Analogous to Ragas context_precision.
    /// </summary>
    public required double ContextPrecision { get; init; }

    /// <summary>
    /// Fraction of ground-truth information captured in the retrieved context (0.0-1.0).
    /// Set to -1.0 when ground truth is unavailable and recall was not evaluated.
    /// Analogous to Ragas context_recall.
    /// </summary>
    public required double ContextRecall { get; init; }

    /// <summary>
    /// Degree to which the generated answer is supported by the retrieved context (0.0-1.0).
    /// Analogous to Ragas faithfulness.
    /// </summary>
    public required double Faithfulness { get; init; }

    /// <summary>
    /// How well the generated answer addresses the original query (0.0-1.0).
    /// Analogous to Ragas answer_relevancy.
    /// </summary>
    public required double AnswerRelevancy { get; init; }

    /// <summary>
    /// Weighted average of the four component metrics. Range 0.0-1.0.
    /// </summary>
    public required double OverallScore { get; init; }

    /// <summary>
    /// LLM-generated reasoning explaining the quality assessment.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>Timestamp when this evaluation was performed.</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }
}
