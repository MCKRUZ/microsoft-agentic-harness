using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Changes.Gates;

/// <summary>
/// Built-in approval gate. Always returns <see cref="GateAction.Defer"/> after
/// dispatching the proposal to the configured <see cref="IChangeApprovalRouter"/>
/// — orchestrator interprets the Defer as the signal to transition into
/// <see cref="ChangeProposalStatus.AwaitingApproval"/> and pause for a human
/// (or external auto-approver) to call <c>ApproveChangeProposalCommand</c> or
/// <c>RejectChangeProposalCommand</c>.
/// </summary>
/// <remarks>
/// <para>
/// PR-2 always defers — the autonomy-tier-driven auto-approve path arrives in
/// PR-4. Until then, "auto-approve" only happens when the proposal's
/// <c>RequiredGates</c> omits the <c>approval</c> key entirely (e.g. Trivial
/// blast radius via <c>DefaultChangeProposalGateResolver</c>); the orchestrator
/// handles that path without invoking the gate at all and records an explicit
/// <c>"auto-approver"</c> entry in the audit.
/// </para>
/// <para>
/// Routing failures (escalation service down, Slack webhook 500) surface as
/// <see cref="GateAction.Fail"/> rather than thrown exceptions — the orchestrator
/// turns Fail into a terminal Rejected, which is safer than leaving the proposal
/// hanging in a state that should have been queued for approval but wasn't.
/// </para>
/// </remarks>
public sealed class ApprovalGate : IChangeProposalGate
{
    /// <summary>Default retry interval reported on the Defer result. Orchestrator does not actually self-loop on the approval gate (it transitions to AwaitingApproval instead), so this is informational for downstream tools.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Stable, scrubbed reason code recorded when the configured
    /// <see cref="IChangeApprovalRouter"/> throws. Used in place of the raw
    /// exception message so that the resulting <see cref="GateResult.Reason"/> —
    /// which the orchestrator persists verbatim into the proposal's
    /// <c>History</c> and the <c>changes.jsonl</c> audit file — never carries
    /// credentials embedded in exception text (e.g. SAS tokens or query-string
    /// secrets in <see cref="System.Net.Http.HttpRequestException"/> URLs raised
    /// by the escalation/Slack/Teams router). The full exception is always
    /// captured via structured logging.
    /// </summary>
    internal const string RoutingFailedReasonCode = "approval_routing_failed";

    private readonly IChangeApprovalRouter _router;
    private readonly ILogger<ApprovalGate> _logger;

    /// <summary>Initializes a new <see cref="ApprovalGate"/>.</summary>
    public ApprovalGate(IChangeApprovalRouter router, ILogger<ApprovalGate> logger)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => WellKnownGateKeys.Approval;

    /// <inheritdoc />
    public GatePhase Phase => GatePhase.Approval;

    /// <inheritdoc />
    public async Task<GateResult> EvaluateAsync(
        ChangeProposal proposal,
        GateContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _router.RouteAsync(proposal, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ApprovalGate routing failed for proposal {ProposalId} (attempt {Attempt}).",
                proposal.Id,
                context.AttemptCount);
            // Persist a stable scrubbed code plus the exception *type* only — never
            // ex.Message. This Reason is copied verbatim into GateDecision.Reason,
            // which is written to changes.jsonl and the proposal History returned to
            // callers. The router calls escalation/Slack/Teams HTTP services whose
            // exception text routinely embeds request URLs with SAS tokens or
            // query-string credentials. The full exception is captured above via
            // structured logging (correlatable by ProposalId/AttemptCount).
            return GateResult.Fail(
                $"{RoutingFailedReasonCode}: {ex.GetType().Name}");
        }

        return GateResult.Defer(
            $"awaiting human approval (attempt {context.AttemptCount})",
            DefaultRetryInterval);
    }
}
