using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Non-blocking human approval gate. Queues an escalation and transitions
/// the step to Blocked. The plan executor polls for resolution.
/// </summary>
public sealed class HumanGateStepExecutor : IPlanStepExecutor
{
    private readonly IEscalationService _escalationService;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<HumanGateStepExecutor> _logger;

    public HumanGateStepExecutor(
        IEscalationService escalationService,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<HumanGateStepExecutor> logger)
    {
        _escalationService = escalationService;
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

        if (step.Configuration is not HumanGateConfig config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for HumanGate executor."
            };
        }

        await _notifier.NotifyStepStartedAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, step.Name, StepType.HumanGate, ct);

        var request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "planner",
            ToolName = step.Name,
            Arguments = BuildArgumentsSummary(upstreamOutputs),
            Description = config.EscalationMessage,
            RiskLevel = config.RiskLevel,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = MapApprovalStrategy(config.ApprovalStrategy),
            Approvers = config.Approvers,
            TimeoutSeconds = (int)config.Timeout.TotalSeconds,
            RequestedAt = DateTimeOffset.UtcNow
        };

        var escalationId = await _escalationService.QueueEscalationAsync(request, ct);
        sw.Stop();

        _logger.LogInformation("Human gate '{Step}' queued escalation {EscalationId}",
            step.Name, escalationId);

        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Blocked,
            Output = JsonSerializer.Serialize(new { escalationId }),
            Duration = sw.Elapsed
        };
    }

    private static IReadOnlyDictionary<string, string> BuildArgumentsSummary(
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var args = new Dictionary<string, string>();
        foreach (var (stepId, output) in upstreamOutputs)
        {
            var summary = output.Length > 200 ? output[..200] + "..." : output;
            args[stepId.Value.ToString()] = summary;
        }
        return args;
    }

    private static ApprovalStrategyType MapApprovalStrategy(ApprovalStrategy strategy) =>
        strategy switch
        {
            ApprovalStrategy.AnyOf => ApprovalStrategyType.AnyOf,
            ApprovalStrategy.AllOf => ApprovalStrategyType.AllOf,
            ApprovalStrategy.Quorum => ApprovalStrategyType.Quorum,
            _ => ApprovalStrategyType.AnyOf
        };
}
