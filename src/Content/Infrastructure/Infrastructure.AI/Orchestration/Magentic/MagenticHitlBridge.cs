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
/// <c>review.Revise(...)</c> downstream in the orchestrator's event subscriber). This is one of
/// two places in the harness where a human's own words are handed to an LLM by design — the other
/// is the tool-call revise carve-out in <c>EscalationToolApprovalRouter</c>, config-gated and off
/// by default; every other free-text reason anywhere else in the escalation subsystem still never
/// reaches the model. Both relays share one format: sanitized through
/// <see cref="ICompositeResponseSanitizer"/> — the same chain that scrubs tool output — and then
/// attributed and delimited by <see cref="Domain.AI.Escalation.HumanFeedbackRelay.Wrap"/> so the
/// text reads as quoted human feedback, never as a system directive.
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
        //
        // Filtered against `approver` — computed above from the request input, before the
        // escalation was even raised — rather than outcome.Approvers alone: that field travels on
        // the same durable record as outcome.Decisions, so checking one against the other adds no
        // independent trust (the same gap EscalationToolApprovalRouter's ComposeRevisionFeedback
        // closed by checking against live config instead of the record's own copy of its roster).
        // This is a channel that relays text to a model by design, worth the extra filter.
        //
        // This bridge only ever raises an escalation for one approver (Approvers = [approver]
        // above), so "is approver still on the live roster" is a single membership test, not a set
        // intersection — no HashSet needed for what can only ever hold zero or one elements.
        var approverIsOnLiveRoster = outcome.Approvers.Contains(approver, ApproverNames.Comparer);
        var source = approverIsOnLiveRoster
            ? outcome.Decisions
                .Where(d => !d.IsApproved && ApproverNames.Comparer.Equals(d.ApproverName, approver))
                .Select(d => new { d.ApproverName, Text = d.Instructions ?? d.Reason })
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Text))
            : null;

        // The fallback below is ours, not human-authored, so it is never run through
        // HumanFeedbackRelay.Wrap — that type's whole contract is attributing text to the human
        // who wrote it, and this string has no such author. Wrap() itself can also return null
        // (blank input, or the sanitizer redacted everything) — same fallback either way.
        var revisionFeedback = source is null
            ? $"Plan rejected ({outcome.ResolutionType})."
            : HumanFeedbackRelay.Wrap(_sanitizer.Sanitize(source.Text!, ToolName).SanitizedContent, source.ApproverName)
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
