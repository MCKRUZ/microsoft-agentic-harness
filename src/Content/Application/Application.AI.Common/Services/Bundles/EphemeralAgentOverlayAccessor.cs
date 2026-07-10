using Domain.AI.Bundles;

namespace Application.AI.Common.Services.Bundles;

/// <summary>
/// Ambient accessor that publishes the <see cref="EphemeralAgentOverlay"/> active for the current async
/// flow, so the overlay-aware registries can resolve a bundle's ephemeral agent and its owned skills
/// without those definitions ever entering the host's persistent registries.
/// </summary>
/// <remarks>
/// <para>
/// Follows the same pattern as the host's other per-flow accessors (<c>ToolGovernanceAccessor</c>,
/// <c>AgentTurnStreamSink</c>, <c>KnowledgeScopeAccessor</c>): an <see cref="AsyncLocal{T}"/> the run
/// path sets at the start of a bundle run and clears in a <c>finally</c>, read at resolution time by the
/// overlay-aware registries. When unset — every non-bundle code path — the registries fall through to
/// the global singletons and behave identically to a host with no bundle concept.
/// </para>
/// <para>
/// Prefer <see cref="Begin"/> to publish an overlay: it restores the previous ambient value on dispose,
/// so nested or sequential runs on the same flow cannot leak an overlay into unrelated work.
/// </para>
/// <para>
/// Caveat for the run path: like any <see cref="AsyncLocal{T}"/>, the overlay is captured into the
/// <c>ExecutionContext</c> of any fire-and-forget or post-turn background work started <em>while it is
/// active</em>, and that captured copy keeps seeing the overlay after the <see cref="Begin"/> scope
/// disposes on the originating flow. Concurrent runs on separate requests are unaffected (each request
/// is its own flow). The run driver must therefore not spawn detached work that should outlive — or be
/// isolated from — the ephemeral agent while the overlay is active; scope the overlay tightly around the
/// turn and let it fall out before any such work is queued.
/// </para>
/// </remarks>
public static class EphemeralAgentOverlayAccessor
{
    private static readonly AsyncLocal<EphemeralAgentOverlay?> s_current = new();

    /// <summary>
    /// The overlay for the current async flow, or <see langword="null"/> when not inside a bundle run.
    /// </summary>
    public static EphemeralAgentOverlay? Current => s_current.Value;

    /// <summary>
    /// Publishes <paramref name="overlay"/> as the ambient overlay for the current async flow and returns
    /// a handle that restores the previous ambient value when disposed. Use with <c>using</c> so the
    /// overlay is guaranteed to be torn down when the run completes, even on exception.
    /// </summary>
    /// <param name="overlay">The overlay to make active; must not be null.</param>
    public static IDisposable Begin(EphemeralAgentOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var previous = s_current.Value;
        s_current.Value = overlay;
        return new OverlayScope(previous);
    }

    private sealed class OverlayScope(EphemeralAgentOverlay? previous) : IDisposable
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
