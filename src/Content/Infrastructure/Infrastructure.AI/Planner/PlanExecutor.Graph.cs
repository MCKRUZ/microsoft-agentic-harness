using System.Collections.Concurrent;
using Domain.AI.Planner;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Pure graph and step-state queries backing the scheduler: dependency/dependent map construction,
/// readiness predicates, upstream output collection, and the atomic Pending → Ready promotion.
/// </summary>
/// <remarks>
/// Every member here is static and side-effect free apart from <see cref="TryMarkReady"/>, which
/// performs one compare-and-swap on the shared state dictionary. Keeping them separate from
/// <c>PlanExecutor.Scheduling.cs</c> leaves that file to the scheduling loop and attempt/retry
/// policy it actually implements.
/// </remarks>
public sealed partial class PlanExecutor
{
    /// <summary>
    /// Builds the forward and reverse adjacency maps for the plan: dependencies per step (what it
    /// waits on) and dependents per step (what it releases, with each edge's type).
    /// </summary>
    private static (Dictionary<PlanStepId, HashSet<PlanStepId>> DependencyMap,
        Dictionary<PlanStepId, List<(PlanStepId Target, EdgeType Type)>> DependentMap) BuildGraphMaps(PlanGraph plan)
    {
        var dependencyMap = new Dictionary<PlanStepId, HashSet<PlanStepId>>();
        var dependentMap = new Dictionary<PlanStepId, List<(PlanStepId, EdgeType)>>();

        foreach (var step in plan.Steps)
        {
            dependencyMap[step.Id] = [];
            dependentMap[step.Id] = [];
        }

        foreach (var edge in plan.Edges)
        {
            dependencyMap[edge.To].Add(edge.From);
            dependentMap[edge.From].Add((edge.To, edge.Type));
        }

        return (dependencyMap, dependentMap);
    }

    /// <summary>
    /// Atomically promotes a step from Pending to Ready. Returns false when another scheduling pass
    /// already claimed it, so a step is enqueued exactly once under concurrent completion callbacks.
    /// </summary>
    private static bool TryMarkReady(PlanStepId stepId, ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
    {
        while (true)
        {
            var current = stepStates.GetValueOrDefault(stepId);
            if (current is null || current.Status != StepExecutionStatus.Pending)
                return false;

            var newState = current with { Status = StepExecutionStatus.Ready };
            if (stepStates.TryUpdate(stepId, newState, current))
                return true;
        }
    }

    /// <summary>Collects the persisted outputs of the step's completed dependencies.</summary>
    private static IReadOnlyDictionary<PlanStepId, string> GetUpstreamOutputs(
        PlanStepId stepId,
        Dictionary<PlanStepId, HashSet<PlanStepId>> dependencyMap,
        ConcurrentDictionary<PlanStepId, string> stepOutputs)
    {
        var outputs = new Dictionary<PlanStepId, string>();
        if (!dependencyMap.TryGetValue(stepId, out var dependencies))
            return outputs;

        foreach (var depId in dependencies)
        {
            if (stepOutputs.TryGetValue(depId, out var output))
                outputs[depId] = output;
        }
        return outputs;
    }

    /// <summary>
    /// Whether every dependency of the step has reached Completed or Skipped — the only two states
    /// that release downstream work.
    /// </summary>
    private static bool IsStepReady(
        PlanStepId stepId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        Dictionary<PlanStepId, HashSet<PlanStepId>> dependencyMap)
    {
        if (!dependencyMap.TryGetValue(stepId, out var dependencies) || dependencies.Count == 0)
            return true;

        return dependencies.All(depId =>
        {
            var depState = stepStates.GetValueOrDefault(depId);
            return depState?.Status is StepExecutionStatus.Completed or StepExecutionStatus.Skipped;
        });
    }

    /// <summary>Whether no step can make further progress, so the scheduling loop may exit.</summary>
    private static bool AllStepsTerminal(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.All(s => s.Status is StepExecutionStatus.Completed
            or StepExecutionStatus.Failed
            or StepExecutionStatus.Skipped
            or StepExecutionStatus.Blocked
            or StepExecutionStatus.Cancelled);

    /// <summary>Whether any step is parked awaiting a human decision.</summary>
    private static bool HasBlockedSteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status == StepExecutionStatus.Blocked);

    /// <summary>Whether any step is still schedulable in this execution.</summary>
    private static bool HasPendingOrReadySteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status is StepExecutionStatus.Pending or StepExecutionStatus.Ready);
}
