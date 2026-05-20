using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Evaluates retrieval quality using Ragas-inspired metrics: context precision,
/// context recall, faithfulness, and answer relevancy via LLM judges.
/// </summary>
public interface IRetrievalQualityEvaluator
{
    /// <summary>
    /// Evaluates the quality of a retrieval+generation cycle.
    /// </summary>
    Task<RetrievalQualityReport> EvaluateAsync(
        string query,
        string answer,
        IReadOnlyList<RerankedResult> context,
        string? groundTruth = null,
        CancellationToken cancellationToken = default);
}
