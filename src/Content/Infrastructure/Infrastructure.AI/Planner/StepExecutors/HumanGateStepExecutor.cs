using System.Diagnostics;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Non-blocking human approval gate. Queues an escalation and transitions the step to Blocked,
/// carrying the escalation identifier in its <see cref="StepExecutionResult.Output"/>.
/// </summary>
/// <remarks>
/// The gate does not poll. The plan executor persists the returned escalation id with the blocked
/// step and, on a subsequent resume, reconciles the block against the escalation's outcome: an
/// approval completes the gate and releases its downstream, a rejection fails it. The gate's job ends
/// the moment the escalation is queued.
/// </remarks>
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

        var request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "planner",
            ToolName = step.Name,
            Arguments = BuildArgumentsSummary(step, upstreamOutputs),
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

            // Written through the shared shape rather than an anonymous object: the plan executor
            // reads this back on resume to reconcile the gate, and the run substrate reads it to learn
            // which decision the run is waiting on. A local literal here drifting from either reader
            // fails silently — the gate simply never releases.
            Output = EscalationStepOutput.Serialize(escalationId),
            Duration = sw.Elapsed
        };
    }

    private const int MaxOutputSummaryLength = 200;

    /// <summary>
    /// What the approver is shown of the gate: each upstream output cut to a short summary, a digest of
    /// the full, uncut outputs, and a note when any was cut (#784). Approving a summary is approving a
    /// rendering; the digest is what lets the approval be matched to the whole data the gate released.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildArgumentsSummary(
        PlanStep step, IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var args = new Dictionary<string, string>();
        var full = new Dictionary<string, string>();
        var shortened = 0;

        foreach (var (stepId, output) in upstreamOutputs)
        {
            var key = stepId.Value.ToString();
            full[key] = output;

            if (output.Length > MaxOutputSummaryLength)
            {
                shortened++;
                args[key] = output[..MaxOutputSummaryLength] + "...";
            }
            else
            {
                args[key] = output;
            }
        }

        if (shortened > 0)
        {
            args[EscalationCallDigest.TruncatedKey] =
                $"{shortened} upstream output(s) shortened; the call digest covers the full outputs";
        }

        // Upstream step ids are Guids, so none can collide with the reserved entries.
        args[EscalationCallDigest.DigestKey] = EscalationCallDigest.Compute(step.Name, full);
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
