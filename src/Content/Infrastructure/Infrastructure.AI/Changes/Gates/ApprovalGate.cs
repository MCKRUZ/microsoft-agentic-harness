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
            return GateResult.Fail(
                $"approval routing failed: {ex.GetType().Name}: {ex.Message}");
        }

        return GateResult.Defer(
            $"awaiting human approval (attempt {context.AttemptCount})",
            DefaultRetryInterval);
    }
}
