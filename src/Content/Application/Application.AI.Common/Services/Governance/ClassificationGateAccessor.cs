using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that bridges the per-turn scoped <see cref="IToolClassificationGate"/> to the agent's
/// converted tool functions.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ToolGovernanceAccessor"/> and <see cref="ProgressGuardAccessor"/>: agents (and their
/// captured tool-invocation lambdas) are cached across turns, so the governed tool wrapper cannot capture a
/// scoped gate at build time — it would go stale. The turn handler sets <see cref="Current"/> to the live
/// scoped gate at the start of each turn and clears it in a <c>finally</c>; the wrapper reads it at
/// invocation time. When unset (a tool invoked outside a governed turn), the wrapper skips classification.
/// </remarks>
public static class ClassificationGateAccessor
{
    private static readonly AsyncLocal<IToolClassificationGate?> s_current = new();

    /// <summary>The classification gate for the current async flow, or null when not inside a governed turn.</summary>
    public static IToolClassificationGate? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>
    /// Publishes <paramref name="gate"/> for the current async flow and returns a handle that restores
    /// the previous ambient value when disposed. A null gate is published as null — a host that
    /// registers no gate still gets a well-formed scope rather than a special case at the call site.
    /// </summary>
    /// <param name="gate">The gate to make active, or null when the host registers none.</param>
    /// <returns>A scope handle. Dispose it to restore whatever was ambient before.</returns>
    /// <remarks>
    /// Prefer this over assigning <see cref="Current"/> and nulling it in a <c>finally</c>: nulling on
    /// teardown disarms whatever an enclosing flow armed, whereas this restores it. See
    /// <see cref="ToolGovernanceAccessor.Begin"/> for the full reasoning.
    /// </remarks>
    public static IDisposable Begin(IToolClassificationGate? gate)
    {
        var previous = s_current.Value;
        s_current.Value = gate;
        return new GateScope(previous);
    }

    private sealed class GateScope(IToolClassificationGate? previous) : IDisposable
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
