using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;

namespace Application.AI.Common.Services.Governance;

// Human approval routing for the one verdict the governor cannot decide alone: a tool call that is
// neither plainly permitted nor plainly forbidden, and that policy says a person must rule on.
//
// Kept separate from the main governor file because it is the only decision path that leaves the
// process and waits on something outside it — everything in ToolInvocationGovernor.cs resolves from
// configuration and in-memory rules and returns immediately.
//
// NOTE: deliberately plain comments, not XML docs. Roslyn merges every partial's class-level
// <summary> into one <member> and publishes whichever file the compiler sees first — which is
// alphabetical, so this file would outrank ToolInvocationGovernor.cs and replace the type's real
// documentation with a note about one private method. No warning is emitted when that happens; it
// was caught here by reading the generated XML. Exactly one partial may carry the class-level doc,
// and it is the main file.
public sealed partial class ToolInvocationGovernor
{
    /// <summary>
    /// The outcome of putting an approval-required verdict to a human.
    /// </summary>
    /// <param name="Granted">Whether a human approved the call.</param>
    /// <param name="Block">
    /// The recorded block to return when <paramref name="Granted"/> is false; null when granted.
    /// </param>
    /// <param name="Reason">Approver attribution, carried onto the final trace record.</param>
    private readonly record struct ApprovalGate(
        bool Granted,
        ToolInvocationDecision? Block,
        string Reason);

    /// <summary>
    /// Puts an approval-required verdict to a human and reports whether they allowed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single place both approval sources converge — the permission resolver's
    /// <c>Ask</c> behaviour and the declarative policy engine's <c>RequireApproval</c> action — so
    /// the two cannot drift into different approval semantics.
    /// </para>
    /// <para>
    /// <strong>An approval is not a verdict; it answers one gate.</strong> This method deliberately
    /// does not return an allow. A human approving an <c>Ask</c> has answered the permission layer
    /// and nothing else — the capability envelope, sandbox capability enforcement, and the policy
    /// engine have not ruled on the call yet. Returning an allow here would let an approver grant a
    /// tool the sandbox never granted the capability for, or one an armed envelope does not list.
    /// The caller resumes the normal pipeline instead, so approval can only ever advance a call to
    /// the checks that were already going to run — never past them.
    /// </para>
    /// </remarks>
    private async ValueTask<ApprovalGate> RequestApprovalAsync(
        string agentId, string toolName, string reason, ToolRiskProfile profile,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var approval = await _approvalRouter
            .RequestApprovalAsync(agentId, toolName, reason, profile.Radius, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (approval.Outcome == ToolApprovalOutcome.Approved)
            return new ApprovalGate(Granted: true, Block: null, approval.Reason);

        // Not approved. Count it against the agent exactly as an unrouted approval verdict always
        // has, so repeated attempts at a tool nobody will approve still trip the denial tracker.
        _denialTracker.RecordDenial(agentId, toolName);
        var block = Blocked(toolName, ToolDecisionOutcome.PendingApproval,
            $"requires approval: {reason} ({approval.Reason})", profile.Radius,
            requiredApproval: true, agentId);

        return new ApprovalGate(Granted: false, block, approval.Reason);
    }
}
