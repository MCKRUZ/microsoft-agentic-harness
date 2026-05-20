using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Evaluates whether an assembled answer is faithful to the retrieved context.
/// </summary>
public interface IAnswerFaithfulnessEvaluator
{
    /// <summary>Evaluate faithfulness of the answer against the supporting context.</summary>
    Task<FaithfulnessEvaluation> EvaluateAsync(
        string answer,
        IReadOnlyList<RerankedResult> supportingContext,
        CancellationToken cancellationToken = default);
}
