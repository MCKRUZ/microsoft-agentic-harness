using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Evaluates a harness candidate against a set of tasks and returns aggregated scores.
/// Each task is run in isolation using the candidate's in-memory skill snapshots.
/// </summary>
public interface IEvaluationService
{
    /// <summary>
    /// Runs each eval task against the candidate's proposed harness configuration,
    /// grades outputs against expected patterns, and writes per-task traces.
    /// </summary>
    Task<EvaluationResult> EvaluateAsync(
        HarnessCandidate candidate,
        IReadOnlyList<EvalTask> evalTasks,
        CancellationToken cancellationToken = default);
}

/// <summary>Aggregated result of evaluating one candidate across all tasks.</summary>
public sealed record EvaluationResult(
    Guid CandidateId,
    double PassRate,
    long TotalTokenCost,
    IReadOnlyList<TaskEvaluationResult> PerExampleResults);

/// <summary>Result for a single eval task run.</summary>
public sealed record TaskEvaluationResult(
    string TaskId,
    bool Passed,
    long TokenCost,
    string? FailureReason = null);
