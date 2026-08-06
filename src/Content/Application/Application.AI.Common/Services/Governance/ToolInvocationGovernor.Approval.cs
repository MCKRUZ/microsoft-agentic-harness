using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Human approval routing for the one verdict the governor cannot decide alone: a tool call that
/// is neither plainly permitted nor plainly forbidden, and that policy says a person must rule on.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from the main governor file because it is the only decision path that leaves the
/// process and waits on something outside it. Everything in <c>ToolInvocationGovernor.cs</c>
/// resolves from configuration and in-memory rules and returns immediately; this suspends the
/// agent's turn until a human answers or the request times out.
/// </para>
/// <para>
/// <strong>The block is the default, not the fallback.</strong> Routing switched off, an empty
/// roster, a refusal, a timeout, and a failed escalation all land on the same
/// <see cref="ToolDecisionOutcome.PendingApproval"/> block that was this verdict's only outcome
/// before routing existed. A host that does not opt in observes byte-identical behaviour; one that
/// does can only ever gain calls a human explicitly said yes to.
/// </para>
/// </remarks>
public sealed partial class ToolInvocationGovernor
{
    /// <summary>
    /// Resolves a verdict of "requires approval" by asking a human, and blocks if that does not
    /// produce an affirmative answer.
    /// </summary>
    /// <remarks>
    /// This is the single place both approval sources converge — the permission resolver's
    /// <c>Ask</c> behaviour and the declarative policy engine's <c>RequireApproval</c> action — so
    /// the two cannot drift into different approval semantics.
    /// </remarks>
    /// <param name="agentId">The agent attempting the call.</param>
    /// <param name="toolName">The tool awaiting a decision.</param>
    /// <param name="reason">Why approval is required, carried to the approver and the trace.</param>
    /// <param name="profile">The tool's risk profile, whose blast radius drives escalation priority.</param>
    /// <param name="arguments">The call arguments, so the approver rules on this specific invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An allow decision only when a human approved; otherwise the recorded block.</returns>
    private async ValueTask<ToolInvocationDecision> ResolveApprovalAsync(
        string agentId, string toolName, string reason, ToolRiskProfile profile,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var approval = await _approvalRouter
            .RequestApprovalAsync(agentId, toolName, reason, profile.Radius, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (approval.Outcome == ToolApprovalOutcome.Approved)
        {
            Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
                $"approved by human: {approval.Reason}", profile.Radius,
                RequiredApproval: true, ApprovalGranted: true, Enforced: true));

            if (_governanceConfig.CurrentValue.EnableAudit)
                _auditService.Log(agentId, toolName, ToolDecisionOutcome.Allowed.ToString());

            return ToolInvocationDecision.Allow();
        }

        // Not approved. Count it against the agent exactly as an unrouted approval verdict always
        // has, so repeated attempts at a tool nobody will approve still trip the denial tracker.
        _denialTracker.RecordDenial(agentId, toolName);
        return Blocked(toolName, ToolDecisionOutcome.PendingApproval,
            $"requires approval: {reason} ({approval.Reason})", profile.Radius,
            requiredApproval: true, agentId);
    }
}
