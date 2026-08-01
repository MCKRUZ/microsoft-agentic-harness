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

    /// <summary>
    /// Publishes <paramref name="governor"/> for the current async flow and returns a handle that
    /// restores the previous ambient value when disposed.
    /// </summary>
    /// <param name="governor">The governor to make active; must not be null.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Prefer this over assigning <see cref="Current"/> and nulling it in a <c>finally</c>.</strong>
    /// The two are not equivalent under nesting: nulling on teardown disarms whatever was armed by an
    /// enclosing flow, whereas this restores it. A nested governed call that finished inside an outer
    /// one's window would otherwise leave the outer call ungoverned for the rest of its life.
    /// </para>
    /// <para>
    /// Mirrors <see cref="CapabilityEnvelopeAccessor.Begin"/>, which has always had this shape.
    /// </para>
    /// </remarks>
    public static IDisposable Begin(IToolInvocationGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(governor);
        var previous = s_current.Value;
        s_current.Value = governor;
        return new GovernorScope(previous);
    }

    private sealed class GovernorScope(IToolInvocationGovernor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            s_current.Value = previous;
        }
    }
}
