using System.Collections.Generic;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
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
/// <para>
/// <b><c>RevisionFeedback</c> is relayed straight to the Magentic manager model</b> (it drives
/// <c>review.Revise(...)</c> downstream in the orchestrator's event subscriber) — unlike the
/// tool-call approval path, where an approver's free text is deliberately never relayed to the
/// model. This bridge is the one place in the harness where a human's own words are handed to
/// an LLM by design, so the text is run through <see cref="ICompositeResponseSanitizer"/> —
/// the same chain that scrubs tool output — before it leaves this method.
/// </para>
/// </remarks>
public sealed class MagenticHitlBridge : IMagenticPlanReviewBridge
{
    private readonly IEscalationService _escalationService;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly ILogger<MagenticHitlBridge> _logger;
    private readonly TimeProvider _timeProvider;
    private const string DefaultApprover = "magentic.plan_review.approver";
    private const int DefaultTimeoutSeconds = 1800;
    private const string ToolName = "magentic.plan_review";

    /// <summary>Creates a bridge backed by the harness escalation service.</summary>
    public MagenticHitlBridge(
        IEscalationService escalationService,
        ICompositeResponseSanitizer sanitizer,
        ILogger<MagenticHitlBridge> logger,
        TimeProvider timeProvider)
    {
        _escalationService = escalationService;
        _sanitizer = sanitizer;
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
            ToolName = ToolName,
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

        // Instructions first, Reason as fallback: a Revise decision carries its steering text on
        // Instructions (required by SubmitEscalationDecisionCommandValidator whenever Verdict is
        // Revise) and may leave Reason blank, while a Deny decision has only ever had Reason.
        // Reading Reason alone would silently drop a reviewer's actual words on exactly the verdict
        // this relay exists to carry, falling back to the generic rejection string instead.
        var rawFeedback = outcome.Decisions
            .Where(d => !d.IsApproved)
            .Select(d => d.Instructions ?? d.Reason)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            ?? $"Plan rejected ({outcome.ResolutionType}).";

        // The fallback string above is ours, not human-authored, but it is cheaper to run every
        // path through one sanitizer call than to reason about which branch needs it.
        var revisionFeedback = _sanitizer.Sanitize(rawFeedback, ToolName).SanitizedContent;

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
