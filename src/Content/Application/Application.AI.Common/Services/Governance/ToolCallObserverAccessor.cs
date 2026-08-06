using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that bridges the per-turn scoped <see cref="IToolCallObserverChain"/> to the
/// agent's converted tool functions.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ToolGovernanceAccessor"/>, <see cref="ClassificationGateAccessor"/>, and
/// <see cref="ProgressGuardAccessor"/>, and exists for the same reason: agents — and the tool
/// invocation lambdas they capture — are cached across turns, so the governed tool wrapper cannot
/// hold a scoped chain captured at build time without it going stale. The turn handler publishes
/// the live scoped chain at the start of each turn and restores the previous value on the way out;
/// the wrapper reads it at invocation time. When unset — a tool invoked outside a governed turn —
/// the wrapper skips observers entirely.
/// </remarks>
public static class ToolCallObserverAccessor
{
    private static readonly AsyncLocal<IToolCallObserverChain?> s_current = new();

    /// <summary>
    /// The observer chain for the current async flow, or null when not inside a governed turn.
    /// </summary>
    public static IToolCallObserverChain? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>
    /// Publishes <paramref name="chain"/> for the current async flow and returns a handle that
    /// restores the previous ambient value when disposed. A null chain is published as null, so a
    /// host that registers none still gets a well-formed scope rather than a special case at the
    /// call site.
    /// </summary>
    /// <param name="chain">The chain to make active, or null when the host registers none.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    /// <remarks>
    /// Prefer this over assigning <see cref="Current"/> and nulling it in a <c>finally</c>: nulling
    /// on teardown disarms whatever an enclosing flow armed, whereas this restores it. See
    /// <see cref="ToolGovernanceAccessor.Begin"/> for the full reasoning.
    /// </remarks>
    public static IDisposable Begin(IToolCallObserverChain? chain)
    {
        var previous = s_current.Value;
        s_current.Value = chain;
        return new ObserverScope(previous);
    }

    private sealed class ObserverScope(IToolCallObserverChain? previous) : IDisposable
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
