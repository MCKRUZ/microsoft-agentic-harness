using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that bridges the per-turn scoped <see cref="IToolCallAdmissionPipeline"/> to the
/// agent's converted tool functions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the admission chain is reached ambiently at all.</strong> Agents — and the tool
/// functions they capture — are cached across turns by <c>IAgentConversationCache</c>, and the wrapper
/// that governs them is built by <c>IToolChainBuilder</c>, which is a singleton. Neither can hold a
/// scoped pipeline: it would go stale on the next turn, or could not be injected at all. The turn
/// handler publishes the live scoped pipeline for the duration of the turn and the governed tool
/// wrapper reads it at invocation time. When unset — a tool invoked outside a governed turn — the
/// wrapper passes through.
/// </para>
/// <para>
/// <strong>This replaced four accessors, one per gate.</strong> Publishing four ambient values meant
/// four chances to arm three of them, and one caller did exactly that: the orchestrated-task handler
/// armed the governor, the classification gate and the observer chain, and never armed the loop guard.
/// There is now one value to publish, so a partially armed turn is not expressible.
/// </para>
/// <para>
/// <strong><see cref="Current"/> is read-only on purpose.</strong> Publishing goes through
/// <see cref="Begin(IToolCallAdmissionPipeline)"/>, which restores the previous value on dispose.
/// Assigning and then nulling in a <c>finally</c> is not equivalent under nesting and is the bug this
/// shape exists to prevent: nulling on teardown disarms whatever an <em>enclosing</em> flow had armed,
/// leaving the outer call ungoverned for the rest of its life. Restoring cannot do that. Mirrors
/// <see cref="CapabilityEnvelopeAccessor.Begin"/>, which has always had this shape.
/// </para>
/// <para>
/// <strong>The pipeline and the agent it belongs to travel together</strong> (<see cref="CurrentAgentId"/>).
/// A nested run — a delegation, a Magentic participant — runs under the request scope of the turn that
/// started it, which names the parent, so it publishes its own id with its own pipeline as one value. A
/// publish without an id carries none and never inherits an enclosing run's: right for the turn handlers,
/// whose agent <em>is</em> the request scope's, and for direct tool invocation, whose armed identity is a
/// synthetic caller that names no agent definition.
/// </para>
/// </remarks>
public static class ToolAdmissionAccessor
{
    private static readonly AsyncLocal<Published?> s_current = new();

    /// <summary>
    /// The admission chain for the current async flow, or null when not inside a governed turn.
    /// </summary>
    public static IToolCallAdmissionPipeline? Current => s_current.Value?.Pipeline;

    /// <summary>
    /// The id of the agent running under <see cref="Current"/> when that run published one — a nested
    /// delegation or Magentic participant — or null when none did (a turn handler's own pipeline, or
    /// no governed run at all). Callers fall back to the request scope's agent id in that case.
    /// </summary>
    public static string? CurrentAgentId => s_current.Value?.AgentId;

    /// <summary>
    /// Publishes <paramref name="pipeline"/> for the current async flow and returns a handle that
    /// restores the previous ambient value when disposed.
    /// </summary>
    /// <param name="pipeline">The admission chain to make active; must not be null.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    public static IDisposable Begin(IToolCallAdmissionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return Publish(new Published(pipeline, AgentId: null));
    }

    /// <summary>
    /// Publishes <paramref name="pipeline"/> together with the agent it governs, for a nested run whose
    /// agent differs from the enclosing request scope's.
    /// </summary>
    /// <param name="pipeline">The admission chain to make active; must not be null.</param>
    /// <param name="agentId">The id of the agent the nested run executes as; must not be blank.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    public static IDisposable Begin(IToolCallAdmissionPipeline pipeline, string agentId)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return Publish(new Published(pipeline, agentId));
    }

    private static AdmissionScope Publish(Published value)
    {
        var previous = s_current.Value;
        s_current.Value = value;
        return new AdmissionScope(previous);
    }

    private sealed record Published(IToolCallAdmissionPipeline Pipeline, string? AgentId);

    private sealed class AdmissionScope(Published? previous) : IDisposable
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
