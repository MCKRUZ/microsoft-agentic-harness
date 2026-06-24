using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that bridges the per-turn scoped <see cref="IToolInvocationGovernor"/> to the
/// agent's converted tool functions.
/// </summary>
/// <remarks>
/// Agents (and their captured tool-invocation lambdas) are cached across turns by
/// <c>IAgentConversationCache</c>, so a tool function cannot capture a scoped governor at build time —
/// it would go stale on later turns. This follows the existing <c>LlmUsageCapture.Current</c> /
/// <c>AgentTurnStreamSink.Current</c> precedent: the turn handler sets <see cref="Current"/> to the
/// live scoped governor at the start of each turn and clears it in a <c>finally</c>, and the governed
/// tool wrapper reads it at invocation time. When unset (a tool invoked outside a governed turn), the
/// wrapper passes through.
/// </remarks>
public static class ToolGovernanceAccessor
{
    private static readonly AsyncLocal<IToolInvocationGovernor?> s_current = new();

    /// <summary>The governor for the current async flow, or null when not inside a governed turn.</summary>
    public static IToolInvocationGovernor? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
