using System.Collections.Concurrent;
using Domain.AI.Planner;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Summary generation and runtime context types for plan execution.
/// </summary>
public sealed partial class PlanExecutor
{
    private static PlanExecutionSummary BuildSummary(
        PlanId planId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        TimeSpan totalDuration)
    {
        var states = stepStates.Values.ToList();
        var hasFailures = states.Any(s => s.Status == StepExecutionStatus.Failed);
        var hasBlocked = states.Any(s => s.Status == StepExecutionStatus.Blocked);
        var hasCancelled = states.Any(s => s.Status == StepExecutionStatus.Cancelled);

        var finalStatus = hasFailures
            ? StepExecutionStatus.Failed
            : hasBlocked
                ? StepExecutionStatus.Blocked
                : hasCancelled
                    ? StepExecutionStatus.Cancelled
                    : StepExecutionStatus.Completed;

        return new PlanExecutionSummary
        {
            PlanId = planId,
            FinalStatus = finalStatus,
            TotalDuration = totalDuration,
            StepStates = states,
            CompletedStepCount = states.Count(s => s.Status == StepExecutionStatus.Completed),
            FailedStepCount = states.Count(s => s.Status == StepExecutionStatus.Failed),
            SkippedStepCount = states.Count(s => s.Status == StepExecutionStatus.Skipped)
        };
    }

    private sealed record PlanExecutionRuntime(
        PlanId PlanId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> StepStates,
        ConcurrentDictionary<PlanStepId, string> StepOutputs,
        Dictionary<PlanStepId, HashSet<PlanStepId>> DependencyMap,
        Dictionary<PlanStepId, List<(PlanStepId Target, EdgeType Type)>> DependentMap,
        Dictionary<PlanStepId, PlanStep> StepLookup,
        ConcurrentQueue<PlanStep> ReadyQueue,
        SemaphoreSlim Concurrency);
}
