using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.AI.Sandbox;

namespace Application.AI.Common.Notifications;

/// <summary>
/// Default <see cref="IPlanProgressNotifier"/> for hosts without a real-time client
/// transport (ConsoleUI, EvalRunner, FoundryHost, MCP server). All progress
/// notifications are dropped silently.
/// </summary>
/// <remarks>
/// Registered as the always-on default in the standard composition root so the
/// scoped <c>PlanExecutor</c>, every keyed step executor, and the planner command
/// handlers (<c>ExecutePlanCommand</c>, <c>CancelPlanCommand</c>, <c>RetryPlanStepCommand</c>)
/// can resolve their dependency unconditionally. The AgentHub host overrides with a
/// SignalR/AG-UI-backed implementation via last-registration-wins.
/// </remarks>
public sealed class NullPlanProgressNotifier : IPlanProgressNotifier
{
    /// <inheritdoc />
    public Task NotifyPlanStartedAsync(PlanId planId, string planName, PlanGraph graph, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyStepStartedAsync(PlanId planId, PlanStepId stepId, string stepName, StepType type, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyStepCompletedAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus status, TimeSpan duration, string? outputSummary, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyStateUpdateAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus previousStatus, StepExecutionStatus newStatus, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifySandboxStatusAsync(PlanId planId, PlanStepId stepId, string toolName, SandboxIsolationLevel isolationLevel, ResourceUsage usage, string? attestationHash, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyPlanCompletedAsync(PlanId planId, TimeSpan totalDuration, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyPlanFailedAsync(PlanId planId, PlanStepId failedStepId, string errorMessage, CancellationToken ct) =>
        Task.CompletedTask;
}
