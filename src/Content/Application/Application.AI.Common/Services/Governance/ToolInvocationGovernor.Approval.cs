using Application.AI.Common.Interfaces.Escalation;
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
    /// <param name="Block">
    /// The recorded block to return when the call was not approved; null when it was. This single
    /// field carries the outcome — a separate "granted" flag would restate it, and the compiler
    /// cannot see that two such fields agree, which is how a null-forgiving <c>!</c> ends up at the
    /// call site.
    /// </param>
    /// <param name="Reason">Approver attribution, carried onto the final trace record.</param>
    /// <param name="Call">
    /// The approval, for execution reporting, when the call was approved by a routed escalation
    /// with a known conversation. Null on a block, and null on an approval this governor cannot
    /// attribute to a conversation.
    /// </param>
    private readonly record struct ApprovalGate(
        ToolInvocationDecision? Block,
        string Reason,
        ApprovedCall? Call = null);

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
    /// <strong>This returns a gate, not a verdict.</strong> Deliberately: the caller decides what an
    /// approval means, and only <see cref="AuthorizeInOrderAsync"/> knows that every deterministic
    /// gate has already passed by the time a human is asked. Returning an allow from here would put
    /// that judgement in the wrong place.
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
            return new ApprovalGate(Block: null, approval.Reason, BuildApprovedCall(agentId, toolName, approval));

        // Not approved. Count it against the agent exactly as an unrouted approval verdict always
        // has, so repeated attempts at a tool nobody will approve still trip the denial tracker.
        _denialTracker.RecordDenial(agentId, toolName);
        var block = Blocked(toolName, ToolDecisionOutcome.PendingApproval,
            $"requires approval: {reason} ({approval.Reason})", profile.Radius,
            requiredApproval: true, agentId);

        return new ApprovalGate(block, approval.Reason);
    }

    /// <summary>
    /// Builds the execution-reporting handle for an approved call, or null when it cannot be
    /// attributed to a conversation.
    /// </summary>
    /// <remarks>
    /// A defensive check on <see cref="ToolApprovalResult.EscalationId"/>, not a null-forgiving
    /// <c>!</c> — <see cref="ApprovalGate"/>'s own doc explains why that matters here: an approval
    /// this governor cannot attribute must degrade to no execution report, not throw.
    /// </remarks>
    private ApprovedCall? BuildApprovedCall(string agentId, string toolName, ToolApprovalResult approval)
    {
        if (approval.EscalationId is not { } escalationId)
            return null;

        if (ApprovalFailureKey.TryCreate(_executionContext.ConversationId, agentId, toolName) is not { } key)
            return null;

        return new ApprovedCall(escalationId, key);
    }
}
