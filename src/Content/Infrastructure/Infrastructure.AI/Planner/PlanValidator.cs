using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Validates a <see cref="PlanGraph"/> with structural checks (cycle detection, referential
/// integrity, unreachable nodes, conditional branch completeness, sub-plan references) and
/// delegates to FluentValidation for step configuration validation. Fail-fast on structural
/// issues; collects all errors for semantic and configuration issues.
/// </summary>
public sealed class PlanValidator : IPlanValidator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlanValidator> _logger;

    public PlanValidator(IServiceProvider serviceProvider, ILogger<PlanValidator> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PlanValidationResult>> ValidateAsync(PlanGraph plan, CancellationToken ct)
    {
        if (plan.Steps.Count == 0)
            return Result<PlanValidationResult>.ValidationFailure(["Plan graph has no steps."]);

        var stepIndex = plan.Steps.ToDictionary(s => s.Id);

        var integrityErrors = ValidateEdgeReferentialIntegrity(plan, stepIndex);
        if (integrityErrors.Count > 0)
            return Result<PlanValidationResult>.ValidationFailure(integrityErrors);

        var (adjacency, inDegree) = BuildGraphMaps(plan);

        var (topologicalOrder, kahnsErrors) = RunKahnsAlgorithm(plan, adjacency, inDegree);
        if (kahnsErrors.Count > 0)
            return Result<PlanValidationResult>.ValidationFailure(kahnsErrors);

        var reachabilityErrors = ValidateReachability(plan, adjacency, inDegree);
        if (reachabilityErrors.Count > 0)
            return Result<PlanValidationResult>.ValidationFailure(reachabilityErrors);

        var semanticErrors = new List<string>();
        ValidateConditionalBranches(plan, semanticErrors);
        ValidateSubPlanReferences(plan, semanticErrors);
        await ValidateStepConfigurations(plan, semanticErrors, ct);

        if (semanticErrors.Count > 0)
            return Result<PlanValidationResult>.ValidationFailure(semanticErrors);

        var criticalPath = ComputeCriticalPath(plan, stepIndex, topologicalOrder);

        _logger.LogDebug(
            "Plan '{PlanName}' validated successfully. Critical path: {CriticalPath}",
            plan.Name, criticalPath);

        return new PlanValidationResult
        {
            IsValid = true,
            EstimatedCriticalPathDuration = criticalPath
        };
    }

    private static (Dictionary<PlanStepId, List<PlanStepId>> Adjacency, Dictionary<PlanStepId, int> InDegree)
        BuildGraphMaps(PlanGraph plan)
    {
        var adjacency = new Dictionary<PlanStepId, List<PlanStepId>>();
        var inDegree = new Dictionary<PlanStepId, int>();

        foreach (var step in plan.Steps)
        {
            adjacency[step.Id] = [];
            inDegree[step.Id] = 0;
        }

        foreach (var edge in plan.Edges)
        {
            adjacency[edge.From].Add(edge.To);
            inDegree[edge.To]++;
        }

        return (adjacency, inDegree);
    }

    private static List<string> ValidateEdgeReferentialIntegrity(
        PlanGraph plan,
        Dictionary<PlanStepId, PlanStep> stepIndex)
    {
        var errors = new List<string>();

        foreach (var edge in plan.Edges)
        {
            if (!stepIndex.ContainsKey(edge.From))
                errors.Add($"Edge references non-existent source step '{edge.From.Value}'.");
            if (!stepIndex.ContainsKey(edge.To))
                errors.Add($"Edge references non-existent target step '{edge.To.Value}'.");
        }

        return errors;
    }

    private static (List<PlanStepId> TopologicalOrder, List<string> Errors) RunKahnsAlgorithm(
        PlanGraph plan,
        Dictionary<PlanStepId, List<PlanStepId>> adjacency,
        Dictionary<PlanStepId, int> inDegree)
    {
        var localInDegree = new Dictionary<PlanStepId, int>(inDegree);

        var roots = localInDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        if (roots.Count == 0)
            return ([], ["No root nodes found — all steps have at least one incoming edge."]);

        var queue = new Queue<PlanStepId>(roots);
        var topologicalOrder = new List<PlanStepId>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            topologicalOrder.Add(node);

            foreach (var successor in adjacency[node])
            {
                localInDegree[successor]--;
                if (localInDegree[successor] == 0)
                    queue.Enqueue(successor);
            }
        }

        if (topologicalOrder.Count < plan.Steps.Count)
        {
            var processed = topologicalOrder.ToHashSet();
            var cycleNodeNames = plan.Steps
                .Where(s => !processed.Contains(s.Id))
                .Select(s => $"'{s.Name}' ({s.Id.Value})")
                .ToList();

            return ([], [$"Cycle detected involving steps: {string.Join(", ", cycleNodeNames)}."]);
        }

        return (topologicalOrder, []);
    }

    private static List<string> ValidateReachability(
        PlanGraph plan,
        Dictionary<PlanStepId, List<PlanStepId>> adjacency,
        Dictionary<PlanStepId, int> inDegree)
    {
        var roots = plan.Steps.Where(s => inDegree[s.Id] == 0).Select(s => s.Id);
        var visited = new HashSet<PlanStepId>();
        var queue = new Queue<PlanStepId>(roots);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!visited.Add(node)) continue;
            foreach (var successor in adjacency[node])
                queue.Enqueue(successor);
        }

        if (visited.Count < plan.Steps.Count)
        {
            var unreachable = plan.Steps
                .Where(s => !visited.Contains(s.Id))
                .Select(s => $"'{s.Name}' ({s.Id.Value})")
                .ToList();

            return [$"Unreachable steps detected: {string.Join(", ", unreachable)}."];
        }

        return [];
    }

    private static void ValidateConditionalBranches(PlanGraph plan, List<string> errors)
    {
        foreach (var step in plan.Steps.Where(s => s.Type == StepType.ConditionalBranch))
        {
            var outgoing = plan.Edges.Where(e => e.From == step.Id).ToList();
            if (!outgoing.Any(e => e.Type == EdgeType.ConditionalTrue))
                errors.Add($"ConditionalBranch step '{step.Name}' ({step.Id.Value}) is missing a ConditionalTrue edge.");
            if (!outgoing.Any(e => e.Type == EdgeType.ConditionalFalse))
                errors.Add($"ConditionalBranch step '{step.Name}' ({step.Id.Value}) is missing a ConditionalFalse edge.");
        }
    }

    private static void ValidateSubPlanReferences(PlanGraph plan, List<string> errors)
    {
        foreach (var step in plan.Steps)
        {
            if (step.Configuration is not SubPlanConfig config) continue;
            if (!config.ChildPlanId.HasValue) continue;

            if (config.ChildPlanId.Value == plan.Id)
                errors.Add($"Step '{step.Name}' ({step.Id.Value}) references its own plan as a sub-plan (self-reference).");

            // Only checks immediate parent. Full ancestor chain validation requires IPlanStateStore.
            if (plan.ParentPlanId.HasValue && config.ChildPlanId.Value == plan.ParentPlanId.Value)
                errors.Add($"Step '{step.Name}' ({step.Id.Value}) references parent plan as a sub-plan (parent reference).");
        }
    }

    private async Task ValidateStepConfigurations(
        PlanGraph plan,
        List<string> errors,
        CancellationToken ct)
    {
        foreach (var step in plan.Steps)
        {
            var validationErrors = step.Configuration switch
            {
                LlmCallConfig config => await ValidateConfig(config, ct),
                ToolUseConfig config => await ValidateConfig(config, ct),
                HumanGateConfig config => await ValidateConfig(config, ct),
                ConditionalBranchConfig config => await ValidateConfig(config, ct),
                SubPlanConfig config => await ValidateConfig(config, ct),
                _ => LogUnknownConfigType(step)
            };

            errors.AddRange(validationErrors.Select(e =>
                $"Step '{step.Name}' ({step.Id.Value}): {e}"));
        }
    }

    private List<string> LogUnknownConfigType(PlanStep step)
    {
        _logger.LogWarning(
            "No validator registered for StepConfiguration type '{ConfigType}' on step '{StepName}' ({StepId})",
            step.Configuration.GetType().Name, step.Name, step.Id.Value);
        return [];
    }

    private async Task<List<string>> ValidateConfig<T>(T config, CancellationToken ct)
        where T : StepConfiguration
    {
        var validator = _serviceProvider.GetService<IValidator<T>>();
        if (validator is null) return [];

        var result = await validator.ValidateAsync(config, ct);
        return result.Errors.Select(e => e.ErrorMessage).ToList();
    }

    private static TimeSpan ComputeCriticalPath(
        PlanGraph plan,
        Dictionary<PlanStepId, PlanStep> stepIndex,
        List<PlanStepId> topologicalOrder)
    {
        if (topologicalOrder.Count == 0)
            return TimeSpan.Zero;

        var predecessors = new Dictionary<PlanStepId, List<PlanStepId>>();
        foreach (var step in plan.Steps)
            predecessors[step.Id] = [];
        foreach (var edge in plan.Edges)
            predecessors[edge.To].Add(edge.From);

        var longestPathTo = new Dictionary<PlanStepId, TimeSpan>();

        foreach (var nodeId in topologicalOrder)
        {
            var maxPredecessorPath = TimeSpan.Zero;
            foreach (var predId in predecessors[nodeId])
            {
                if (longestPathTo[predId] > maxPredecessorPath)
                    maxPredecessorPath = longestPathTo[predId];
            }

            longestPathTo[nodeId] = maxPredecessorPath + stepIndex[nodeId].Timeout;
        }

        return longestPathTo.Values.Max();
    }
}
