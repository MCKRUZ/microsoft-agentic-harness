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

            // A successful Result only means the child executor ran to a conclusion — the plan's
            // actual outcome lives in FinalStatus. A child that failed, blocked, or was cancelled
            // (or was left with non-terminal steps) now returns Result.Success with a non-Completed
            // FinalStatus, so the parent step is Completed ONLY when the child genuinely completed.
            // Keying off IsSuccess alone would mark the parent step Completed for a failed child —
            // the same "plan lies about success" bug, one level up.
            if (childResult.IsSuccess && childResult.Value!.FinalStatus == StepExecutionStatus.Completed)
            {
                return new StepExecutionResult
                {
                    Status = StepExecutionStatus.Completed,
                    Output = JsonSerializer.Serialize(childResult.Value),
                    Duration = sw.Elapsed
                };
            }

            var errorMessage = childResult.IsSuccess
                ? $"Child plan {childPlanId.Value} did not complete: final status {childResult.Value!.FinalStatus}."
                : childResult.Errors.Count > 0 ? string.Join("; ", childResult.Errors) : "Child plan execution failed.";

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = errorMessage,
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
