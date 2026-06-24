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
    /// <returns>
    /// An allow decision, or a deny decision carrying a model-facing message to return in place of
    /// the tool result. When enforcement is disabled the governor records the would-be decision but
    /// always returns <see cref="ToolInvocationDecision.Allow"/>.
    /// </returns>
    ValueTask<ToolInvocationDecision> AuthorizeAsync(string toolName, CancellationToken cancellationToken);

    /// <summary>Snapshots the governance decisions recorded so far for this turn.</summary>
    GovernanceTrace GetTrace();

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
