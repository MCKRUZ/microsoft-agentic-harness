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
/// <see cref="Begin"/>, which restores the previous value on dispose. Assigning and then nulling in a
/// <c>finally</c> is not equivalent under nesting and is the bug this shape exists to prevent: nulling
/// on teardown disarms whatever an <em>enclosing</em> flow had armed, leaving the outer call ungoverned
/// for the rest of its life. Restoring cannot do that. Mirrors
/// <see cref="CapabilityEnvelopeAccessor.Begin"/>, which has always had this shape.
/// </para>
/// </remarks>
public static class ToolAdmissionAccessor
{
    private static readonly AsyncLocal<IToolCallAdmissionPipeline?> s_current = new();

    /// <summary>
    /// The admission chain for the current async flow, or null when not inside a governed turn.
    /// </summary>
    public static IToolCallAdmissionPipeline? Current => s_current.Value;

    /// <summary>
    /// Publishes <paramref name="pipeline"/> for the current async flow and returns a handle that
    /// restores the previous ambient value when disposed.
    /// </summary>
    /// <param name="pipeline">The admission chain to make active; must not be null.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    public static IDisposable Begin(IToolCallAdmissionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var previous = s_current.Value;
        s_current.Value = pipeline;
        return new AdmissionScope(previous);
    }

    private sealed class AdmissionScope(IToolCallAdmissionPipeline? previous) : IDisposable
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
