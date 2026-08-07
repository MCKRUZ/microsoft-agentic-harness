using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Authorizes individual tool invocations on the agent's live tool-call path and records each
/// decision for the turn's <see cref="GovernanceTrace"/>.
/// </summary>
/// <remarks>
/// <para>
/// This closes the gap where the harness's tool governance (permission ACLs, graded-autonomy risk
/// gating, declarative policy, approval/escalation, capability enforcement) only ran on MediatR
/// <c>IToolRequest</c> commands — which the agent's autonomous tool calls never produce. The governor
/// runs that same logic at the one chokepoint every agent tool flows through (the converted
/// <see cref="Microsoft.Extensions.AI.AITool"/> invocation), so a risk check actually precedes
/// execution.
/// </para>
/// <para>
/// Scoped to one agent turn. The implementation reads the ambient <c>IAgentExecutionContext</c> for
/// the agent identity and accumulates a per-turn decision trace exposed via <see cref="GetTrace"/>.
/// </para>
/// </remarks>
public interface IToolInvocationGovernor
{
    /// <summary>
    /// Decides whether the named tool may execute. Records the decision on the turn trace.
    /// </summary>
    /// <param name="toolName">The tool the agent is attempting to invoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="arguments">
    /// The call arguments, when the caller has them. Used only to describe the specific invocation
    /// to a human when a verdict is routed for approval via <see cref="IToolApprovalRouter"/> —
    /// approving a tool <em>name</em> tells an approver nothing, approving a tool and its target
    /// tells them everything. No authorization decision is derived from this: the permission,
    /// risk, capability, envelope, and policy checks all key off the tool name alone, so passing
    /// arguments cannot change whether a call is allowed.
    /// </param>
    /// <returns>
    /// An allow decision, or a deny decision carrying a model-facing message to return in place of
    /// the tool result. When enforcement is disabled the governor records the would-be decision but
    /// always returns <see cref="ToolInvocationDecision.Allow"/>.
    /// </returns>
    /// <remarks>
    /// <paramref name="arguments"/> is optional so the callers that authorize by capability name
    /// rather than by a model-supplied argument set — the plan-step executors — are unaffected.
    /// </remarks>
    ValueTask<ToolInvocationDecision> AuthorizeAsync(
        string toolName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? arguments = null);

    /// <summary>Snapshots the governance decisions recorded so far for this turn.</summary>
    GovernanceTrace GetTrace();

    /// <summary>
    /// Records that a gate running <em>after</em> this governor refused a call the governor had
    /// already allowed, so the turn's trace reflects what happened rather than what was authorized.
    /// </summary>
    /// <param name="toolName">The tool that was stopped.</param>
    /// <param name="reason">Operator-facing explanation, for the trace and audit only.</param>
    /// <remarks>
    /// <para>
    /// The governor is not the last word on a tool call — the classification gate, the progress
    /// guard, and the host's own <see cref="IToolCallObserver"/> rules all run after it and can each
    /// stop a call it permitted. Without this the trace would report such a call as
    /// <see cref="ToolDecisionOutcome.Allowed"/>, because that is genuinely what the governor
    /// decided, and every consumer of the trace — bundle-run governance reporting, the dashboard,
    /// the audit — would be wrong for exactly the calls a safety rule stopped.
    /// </para>
    /// <para>
    /// This does not revoke the earlier record; both are kept. The governor did allow it, something
    /// downstream did not, and an audit trail that shows only one of those facts is telling half the
    /// story.
    /// </para>
    /// </remarks>
    void RecordDownstreamBlock(string toolName, string reason);

    /// <summary>
    /// Clears the recorded decisions so the next turn starts clean. The governor is registered
    /// scoped, but nested MediatR sends within a conversation share one DI scope (and thus one
    /// governor instance), so a multi-turn conversation must reset between turns — otherwise
    /// <see cref="GetTrace"/> returns the cumulative list and per-turn traces double-count when
    /// aggregated. Mirrors the per-turn reset of the adjacent scoped <c>ILlmUsageCapture</c>.
    /// </summary>
    void Reset();
}

/// <summary>
/// The result of authorizing a single tool invocation.
/// </summary>
/// <param name="IsAllowed">Whether the tool may execute.</param>
/// <param name="DeniedMessage">
/// When denied, the message returned to the model in place of the tool result (the same string-result
/// shape the tool converter already uses for errors). Null when allowed.
/// </param>
public sealed record ToolInvocationDecision(bool IsAllowed, string? DeniedMessage = null)
{
    /// <summary>An allow decision.</summary>
    public static ToolInvocationDecision Allow() => new(true);

    /// <summary>A deny decision carrying the model-facing explanation.</summary>
    public static ToolInvocationDecision Deny(string deniedMessage) => new(false, deniedMessage);
}
