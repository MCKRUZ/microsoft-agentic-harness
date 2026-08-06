using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Turns an approval-required verdict on the agent's live tool-call path into an actual question to
/// a human, and reports what they decided.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IToolInvocationGovernor"/> has always been able to conclude "this call requires
/// approval" — and then had nowhere to send it, so it recorded the outcome and blocked. The
/// escalation subsystem that asks humans (roster, approval strategies, timeout actions, Teams/Slack
/// and dashboard notification, durable state) was built and wired for exactly this caller —
/// <c>EscalationRequest</c> carries <c>ToolName</c> and <c>Arguments</c> — but the tool path never
/// called it. This interface is that missing call.
/// </para>
/// <para>
/// <strong>It can only ever loosen a block, never a grant.</strong> The governor consults this only
/// on a verdict it was already going to refuse. Every non-approval answer — routing disabled, no
/// roster, denial, timeout, or an exception reaching the escalation service — leaves the call
/// blocked. There is no path through this interface that permits a call the governor would
/// otherwise have allowed anyway.
/// </para>
/// </remarks>
public interface IToolApprovalRouter
{
    /// <summary>
    /// Raises a human approval request for a tool call the governor will not auto-approve, and waits
    /// for the decision.
    /// </summary>
    /// <param name="agentId">The agent attempting the call, recorded on the escalation.</param>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="reason">Why approval is required, shown to the approver.</param>
    /// <param name="radius">The tool's blast radius, mapped to escalation risk and priority.</param>
    /// <param name="arguments">
    /// The call arguments, so the approver decides on the specific invocation rather than on the
    /// tool name alone. Sanitized before display. Null when the caller has no arguments to offer
    /// (the plan-step executors authorize by capability name, not by a model-supplied argument set).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="ToolApprovalOutcome.Approved"/> only on an affirmative human decision;
    /// <see cref="ToolApprovalOutcome.Denied"/> when a human refused or the request timed out; and
    /// <see cref="ToolApprovalOutcome.NotRouted"/> when routing is switched off or unusable, which
    /// tells the governor to apply its own pre-existing block.
    /// </returns>
    ValueTask<ToolApprovalResult> RequestApprovalAsync(
        string agentId,
        string toolName,
        string reason,
        BlastRadius radius,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}

/// <summary>What became of a routed tool-approval request.</summary>
public enum ToolApprovalOutcome
{
    /// <summary>
    /// No approval was sought — routing is disabled, or enabled but unusable (no approver roster).
    /// The governor applies its own block, unchanged from before this feature existed.
    /// </summary>
    NotRouted,

    /// <summary>A human approved the call. It may proceed.</summary>
    Approved,

    /// <summary>
    /// A human refused, the request timed out, or the escalation service failed. The call is blocked.
    /// </summary>
    Denied
}

/// <summary>
/// The result of routing one tool call to human approval.
/// </summary>
/// <param name="Outcome">What became of the request.</param>
/// <param name="Reason">
/// Operator-facing explanation for the trace and audit — an approver's identity, "timed out", or the
/// reason routing was skipped. Never relayed to the model.
/// </param>
/// <param name="EscalationId">
/// The escalation raised, when one was. Null when <see cref="Outcome"/> is
/// <see cref="ToolApprovalOutcome.NotRouted"/>, and null when the request failed before an
/// escalation existed.
/// </param>
public sealed record ToolApprovalResult(
    ToolApprovalOutcome Outcome,
    string Reason,
    Guid? EscalationId = null)
{
    /// <summary>Routing did not run; the caller keeps its own blocking behaviour.</summary>
    public static ToolApprovalResult NotRouted(string reason) =>
        new(ToolApprovalOutcome.NotRouted, reason);

    /// <summary>A human approved the call.</summary>
    public static ToolApprovalResult Approved(string reason, Guid escalationId) =>
        new(ToolApprovalOutcome.Approved, reason, escalationId);

    /// <summary>The call was refused, timed out, or could not obtain a decision.</summary>
    public static ToolApprovalResult Denied(string reason, Guid? escalationId = null) =>
        new(ToolApprovalOutcome.Denied, reason, escalationId);
}
