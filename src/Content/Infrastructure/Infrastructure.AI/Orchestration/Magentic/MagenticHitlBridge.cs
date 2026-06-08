using System.Collections.Generic;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Orchestration.Magentic;

/// <summary>
/// HITL bridge mapping a Magentic plan-review pause to the harness's existing
/// <see cref="IEscalationService"/>. Production implementation of
/// <see cref="IMagenticPlanReviewBridge"/>.
/// </summary>
/// <remarks>
/// <para>
/// Build a synthetic <see cref="EscalationRequest"/> per pause, dispatch via
/// <see cref="IEscalationService.RequestEscalationAsync"/> (which blocks on the
/// human decision), and translate the resulting <see cref="EscalationOutcome"/>
/// into a <see cref="MagenticPlanReviewOutcome"/> the orchestrator can hand
/// back to MAF.
/// </para>
/// <para>
/// Mapping rules:
/// <list type="bullet">
/// <item><description><c>ToolName = "magentic.plan_review"</c> — stable discriminator for audit reporting.</description></item>
/// <item><description><c>RiskLevel</c> derived from <c>IsStalled</c>: stalled = <see cref="RiskLevel.High"/>; initial = <see cref="RiskLevel.Medium"/>.</description></item>
/// <item><description><c>Priority = <see cref="EscalationPriority.Blocking"/></c> — workflow blocks on the result.</description></item>
/// <item><description>Revision feedback: first denial decision's <see cref="ApproverDecision.Reason"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class MagenticHitlBridge : IMagenticPlanReviewBridge
{
    private readonly IEscalationService _escalationService;
    private readonly ILogger<MagenticHitlBridge> _logger;
    private readonly TimeProvider _timeProvider;
    private const string DefaultApprover = "magentic.plan_review.approver";
    private const int DefaultTimeoutSeconds = 1800;

    /// <summary>Creates a bridge backed by the harness escalation service.</summary>
    public MagenticHitlBridge(
        IEscalationService escalationService,
        ILogger<MagenticHitlBridge> logger,
        TimeProvider timeProvider)
    {
        _escalationService = escalationService;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<MagenticPlanReviewOutcome> RequestPlanReviewAsync(
        MagenticPlanReviewInput input,
        CancellationToken ct)
    {
        var approver = string.IsNullOrEmpty(input.Approver) ? DefaultApprover : input.Approver;
        var request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = $"magentic:{input.WorkflowName}",
            ToolName = "magentic.plan_review",
            Arguments = BuildArguments(input),
            Description = BuildDescription(input),
            RiskLevel = input.IsStalled ? RiskLevel.High : RiskLevel.Medium,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = ApprovalStrategyType.AnyOf,
            Approvers = new[] { approver },
            TimeoutSeconds = input.TimeoutSeconds ?? DefaultTimeoutSeconds,
            TimeoutAction = EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = _timeProvider.GetUtcNow(),
            OriginatingDecision = null
        };

        _logger.LogInformation(
            "Magentic plan-review escalation queued: workflow={WorkflowId} stalled={IsStalled} escalationId={EscalationId}",
            input.WorkflowId,
            input.IsStalled,
            request.EscalationId);

        var outcome = await _escalationService.RequestEscalationAsync(request, ct).ConfigureAwait(false);

        if (outcome.IsApproved)
        {
            return new MagenticPlanReviewOutcome { Approved = true };
        }

        var revisionFeedback = outcome.Decisions
            .Where(d => !d.Approved)
            .Select(d => d.Reason)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            ?? $"Plan rejected ({outcome.ResolutionType}).";

        return new MagenticPlanReviewOutcome
        {
            Approved = false,
            RevisionFeedback = revisionFeedback
        };
    }

    private static IReadOnlyDictionary<string, string> BuildArguments(MagenticPlanReviewInput input)
    {
        var dict = new Dictionary<string, string>
        {
            ["workflow_id"] = input.WorkflowId.ToString(),
            ["workflow_name"] = input.WorkflowName,
            ["is_stalled"] = input.IsStalled ? "true" : "false"
        };
        if (!string.IsNullOrEmpty(input.ProgressLedgerSummary))
        {
            dict["progress_ledger"] = input.ProgressLedgerSummary;
        }
        return dict;
    }

    private static string BuildDescription(MagenticPlanReviewInput input)
    {
        var stallSegment = input.IsStalled ? " (stall-triggered)" : " (initial signoff)";
        return $"Magentic plan-review for workflow '{input.WorkflowName}'{stallSegment}.";
    }
}
