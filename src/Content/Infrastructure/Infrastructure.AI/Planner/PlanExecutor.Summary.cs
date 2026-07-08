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
        var hasNonTerminal = states.Any(s => s.Status is StepExecutionStatus.Pending
            or StepExecutionStatus.Ready
            or StepExecutionStatus.Running);

        // Blocked/Cancelled are legitimate terminal outcomes and take precedence — a Blocked step
        // with a waiting Pending downstream is a paused plan, not a failure. But if the scheduler
        // exits with non-terminal steps and nothing explains the pause (no failure, no block, no
        // cancellation), the plan did NOT complete: report Failed rather than dishonestly Completed.
        var finalStatus = hasFailures
            ? StepExecutionStatus.Failed
            : hasBlocked
                ? StepExecutionStatus.Blocked
                : hasCancelled
                    ? StepExecutionStatus.Cancelled
                    : hasNonTerminal
                        ? StepExecutionStatus.Failed
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
