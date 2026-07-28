using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Invokes a child plan in an isolated DI scope with depth limiting.
/// The parent step blocks while the child plan executes.
/// </summary>
/// <remarks>
/// The ambient capability envelope flows into the child execution on its own — it is carried by an
/// <c>AsyncLocal</c>, which crosses DI scope boundaries — so an enveloped parent's grant confines the
/// child identically (a tool denied in the parent stays denied in every sub-plan). The governance
/// <em>identity</em> does not flow by itself: <see cref="IAgentExecutionContext"/> is DI-scoped and the
/// child runs in a fresh scope, whose context would otherwise be empty — and the tool-invocation
/// governor fails closed on identity-less tool calls whenever an envelope is ambient. This executor
/// therefore re-stamps the parent's identity onto the child scope, so the child is governed as the same
/// principal: granted tools keep working, denied tools stay denied.
/// </remarks>
public sealed class SubPlanStepExecutor : IPlanStepExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlanStateStore _planStateStore;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly IAgentExecutionContext _agentContext;
    private readonly ILogger<SubPlanStepExecutor> _logger;

    public SubPlanStepExecutor(
        IServiceScopeFactory scopeFactory,
        IPlanStateStore planStateStore,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        IAgentExecutionContext agentContext,
        ILogger<SubPlanStepExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _planStateStore = planStateStore;
        _notifier = notifier;
        _executionContext = executionContext;
        _agentContext = agentContext;
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

        PropagateGovernanceIdentity(scope.ServiceProvider, childPlanId.Value);

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
            // Full detail stays in the structured log; only a stable code is persisted onto the step,
            // because step error state is returned to callers and a child plan's failure can surface
            // EF Core connection strings from the state store.
            _logger.LogError(ex, "Child plan execution threw for step {Step}", step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = PlanStepErrors.SubPlanFailed,
                Duration = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Stamps the parent's governance identity onto the child scope's execution context, so tool
    /// governance inside the sub-plan resolves against the same principal as the parent. No-op when
    /// the parent carries no identity (an ungoverned direct <c>IPlanExecutor</c> run) — the child then
    /// behaves exactly as it did before envelope confinement existed.
    /// </summary>
    private void PropagateGovernanceIdentity(IServiceProvider childServices, PlanId childPlanId)
    {
        if (string.IsNullOrEmpty(_agentContext.AgentId))
            return;

        var childAgentContext = childServices.GetRequiredService<IAgentExecutionContext>();
        childAgentContext.Initialize(
            _agentContext.AgentId,
            _agentContext.ConversationId ?? childPlanId.Value.ToString(),
            _agentContext.TurnNumber ?? 1);
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
