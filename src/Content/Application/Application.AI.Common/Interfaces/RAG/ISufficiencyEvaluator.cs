using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Evaluates whether retrieved context is sufficient to answer a sub-query.
/// Returns a score between 0.0 (insufficient) and 1.0 (fully sufficient).
/// </summary>
public interface ISufficiencyEvaluator
{
    /// <summary>Evaluate sufficiency of retrieved results for a sub-query.</summary>
    Task<double> EvaluateAsync(
        string subQuery,
        IReadOnlyList<RetrievalResult> results,
        CancellationToken cancellationToken = default);
}
