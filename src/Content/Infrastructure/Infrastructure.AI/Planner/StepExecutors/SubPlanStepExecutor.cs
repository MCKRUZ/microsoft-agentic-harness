using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Invokes a child plan in an isolated DI scope with depth limiting.
/// The parent step blocks while the child plan executes.
/// </summary>
public sealed class SubPlanStepExecutor : IPlanStepExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlanStateStore _planStateStore;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<SubPlanStepExecutor> _logger;

    public SubPlanStepExecutor(
        IServiceScopeFactory scopeFactory,
        IPlanStateStore planStateStore,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<SubPlanStepExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _planStateStore = planStateStore;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (step.Configuration is not SubPlanConfig config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for SubPlan executor."
            };
        }

        if (_executionContext.Depth >= _executionContext.MaxDepth)
        {
            _logger.LogWarning("Sub-plan depth limit exceeded at depth {Depth} for step {Step}",
                _executionContext.Depth, step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = $"Maximum sub-plan depth ({_executionContext.MaxDepth}) exceeded."
            };
        }

        await _notifier.NotifyStepStartedAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, step.Name, StepType.SubPlanInvocation, ct);

        var childPlanId = await ResolveChildPlanId(config, ct);
        if (childPlanId is null)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = "Could not resolve child plan: no ChildPlanId or InlinePlanDefinition provided."
            };
        }

        using var scope = _scopeFactory.CreateScope();
        var childContext = new PlanExecutionContext
        {
            Depth = _executionContext.Depth + 1,
            MaxDepth = _executionContext.MaxDepth,
            CurrentPlanId = childPlanId
        };

        var childExecutor = scope.ServiceProvider.GetRequiredService<IPlanExecutor>();

        try
        {
            var childResult = await childExecutor.ExecuteAsync(childPlanId.Value, childContext, ct);
            sw.Stop();

            if (childResult.IsSuccess)
            {
                return new StepExecutionResult
                {
                    Status = StepExecutionStatus.Completed,
                    Output = childResult.Value is not null ? JsonSerializer.Serialize(childResult.Value) : null,
                    Duration = sw.Elapsed
                };
            }

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = childResult.Errors.Count > 0 ? string.Join("; ", childResult.Errors) : "Child plan execution failed.",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Child plan execution threw for step {Step}", step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = $"Child plan exception: {ex.Message}",
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<PlanId?> ResolveChildPlanId(SubPlanConfig config, CancellationToken ct)
    {
        if (config.ChildPlanId is not null)
            return config.ChildPlanId;

        if (config.InlinePlanDefinition is not null)
        {
            var saveResult = await _planStateStore.SavePlanAsync(config.InlinePlanDefinition, ct);
            if (saveResult.IsSuccess)
                return config.InlinePlanDefinition.Id;
        }

        return null;
    }
}
