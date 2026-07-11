using Domain.AI.Bundles;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that publishes the <see cref="CapabilityEnvelope"/> in force for the current async
/// flow, so the permission gate chain and the tool-chain builder can enforce a bundle run's per-caller
/// grant without threading the envelope through every signature.
/// </summary>
/// <remarks>
/// <para>
/// Follows the same pattern as the host's other per-flow accessors (<c>ToolGovernanceAccessor</c>,
/// <c>EphemeralAgentOverlayAccessor</c>, <c>KnowledgeScopeAccessor</c>): an <see cref="AsyncLocal{T}"/>
/// the run path sets at the start of a bundle run and clears in a <c>finally</c>, read at enforcement
/// time. When unset — every non-bundle code path — the gate chain and tool builder see no envelope and
/// behave identically to a host with no bundle concept, so this adds zero behaviour off the bundle path.
/// </para>
/// <para>
/// Prefer <see cref="Begin"/> to publish an envelope: it restores the previous ambient value on dispose,
/// so nested or sequential runs on the same flow cannot leak an envelope into unrelated work.
/// </para>
/// <para>
/// Caveat, identical to the other ambient accessors: like any <see cref="AsyncLocal{T}"/>, the envelope
/// is captured into the <c>ExecutionContext</c> of any fire-and-forget work started <em>while it is
/// active</em>. Scope the envelope tightly around the turn and let it fall out before queuing detached
/// work that should not run under the bundle's grant.
/// </para>
/// <para>
/// <strong>Deferred-execution contract (for the run/streaming paths).</strong> The scope must stay open for
/// the <em>entire</em> execution it governs, not merely the synchronous call that starts it. If a run returns
/// a deferred <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/> / stream, disposing the scope when
/// that method returns clears the envelope before the streamed tool calls run — the governor (which forces
/// enforcement on precisely from the envelope's presence) would then see no envelope and fail <em>open</em>.
/// Keep the <c>using</c> around the full enumeration (dispose in the stream's <c>finally</c>), never around
/// the call that merely produces the stream.
/// </para>
/// </remarks>
public static class CapabilityEnvelopeAccessor
{
    private static readonly AsyncLocal<CapabilityEnvelope?> s_current = new();

    /// <summary>
    /// The envelope for the current async flow, or <see langword="null"/> when not inside a bundle run.
    /// A null value means "no restriction" for the reading component — enforcement only engages when a
    /// bundle run has published an envelope.
    /// </summary>
    public static CapabilityEnvelope? Current => s_current.Value;

    /// <summary>
    /// Publishes <paramref name="envelope"/> as the ambient envelope for the current async flow and
    /// returns a handle that restores the previous ambient value when disposed. Use with <c>using</c> so
    /// the envelope is guaranteed to be torn down when the run completes, even on exception.
    /// </summary>
    /// <param name="envelope">The envelope to make active; must not be null.</param>
    public static IDisposable Begin(CapabilityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var previous = s_current.Value;
        s_current.Value = envelope;
        return new EnvelopeScope(previous);
    }

    private sealed class EnvelopeScope(CapabilityEnvelope? previous) : IDisposable
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
