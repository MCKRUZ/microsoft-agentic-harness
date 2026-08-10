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
/// the agent identity and writes every decision to the turn's <see cref="IGovernanceTraceRecorder"/>,
/// which is where the trail is read from and reset. This type decides; it does not remember.
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
    // An allow carries no per-call state, so one instance serves every call. This sits on the agent's
    // hot path and is now reached two to three times per permitted call (governor, then the observer
    // chain, then the composed helper), which is three identical allocations for a value that is
    // immutable and indistinguishable between calls.
    private static readonly ToolInvocationDecision AllowedDecision = new(true);

    /// <summary>An allow decision.</summary>
    public static ToolInvocationDecision Allow() => AllowedDecision;

    /// <summary>A deny decision carrying the model-facing explanation.</summary>
    public static ToolInvocationDecision Deny(string deniedMessage) => new(false, deniedMessage);
}
