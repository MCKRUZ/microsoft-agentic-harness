using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.MetaHarness;

/// <summary>
/// Compares an evaluated candidate's score against the current best, determines if it
/// represents an improvement, and persists the final scored candidate.
/// Final step in the optimization iteration workflow.
/// </summary>
public sealed class ScoreCandidateExecutor(
    IHarnessCandidateRepository candidateRepository)
    : Executor<EvaluationStepOutput, IterationOutcome>("ScoreCandidate")
{
    /// <summary>
    /// Persists the evaluated candidate and compares its score against the current best
    /// to determine if the iteration produced an improvement.
    /// </summary>
    /// <param name="message">The evaluation output containing the scored candidate.</param>
    /// <param name="context">The MAF workflow context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="IterationOutcome"/> indicating whether the candidate is an improvement.</returns>
    public override async ValueTask<IterationOutcome> HandleAsync(
        EvaluationStepOutput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // Fetch the prior best BEFORE persisting this candidate. Saving first would
        // include the just-evaluated candidate in GetBestAsync's pool, so the
        // improvement comparison below would always measure the candidate against
        // itself and never report an improvement.
        var currentBest = await candidateRepository.GetBestAsync(
            message.Candidate.OptimizationRunId,
            cancellationToken);

        await candidateRepository.SaveAsync(message.Candidate, cancellationToken);

        var score = message.Candidate.BestScore ?? 0.0;
        var bestScore = currentBest?.BestScore ?? 0.0;
        var isImprovement = currentBest is null || score > bestScore;

        return new IterationOutcome(
            Candidate: message.Candidate,
            Score: score,
            IsImprovement: isImprovement,
            Reasoning: $"Iteration {message.Candidate.Iteration}: " +
                       $"score={score:F3}, best={bestScore:F3}, " +
                       $"improvement={isImprovement}");
    }
}
