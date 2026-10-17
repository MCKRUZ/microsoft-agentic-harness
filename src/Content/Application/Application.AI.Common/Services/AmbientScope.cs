namespace Application.AI.Common.Services;

/// <summary>
/// Publishes a value into an <see cref="AsyncLocal{T}"/> slot and hands back the handle that restores
/// what was there before — the one implementation of the pattern every per-flow ambient accessor needs.
/// </summary>
/// <remarks>
/// <para>
/// Eleven accessors (the capability envelope, tool admission, the MCP tool-list cache, the ephemeral
/// skill and agent overlays, the bundle run id, the evidence-read directory, the request scope, the
/// current skill, the workspace context and the knowledge scope) each hand-wrote the same nested class:
/// remember the previous value, publish the new one, and on dispose put the previous one back, guarded
/// against a double dispose. The copies had already started to differ (one had no double-dispose guard).
/// A change to the disposal contract now lands once.
/// </para>
/// <para>
/// <strong>Restores the previous value, never clears to null.</strong> Scopes nest: an inner scope that
/// ends must put the enclosing scope's value back, not leave the flow with nothing published. The restore
/// writes to the slot from whichever flow disposes the handle; dispose it on the flow that began it, the
/// same constraint every one of the hand-written copies had.
/// </para>
/// </remarks>
public static class AmbientScope
{
    /// <summary>
    /// Sets <paramref name="slot"/> to <paramref name="value"/> for the current async flow.
    /// </summary>
    /// <typeparam name="T">The slot's value type; every ambient slot in the codebase holds a reference type.</typeparam>
    /// <param name="slot">The ambient slot to publish into.</param>
    /// <param name="value">The value to publish.</param>
    /// <returns>A handle that restores the slot's previous value when disposed. Disposing twice is harmless.</returns>
    public static IDisposable Begin<T>(AsyncLocal<T?> slot, T? value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(slot);

        var previous = slot.Value;
        slot.Value = value;
        return new Restore<T>(slot, previous);
    }

    private sealed class Restore<T>(AsyncLocal<T?> slot, T? previous) : IDisposable
        where T : class
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            slot.Value = previous;
        }
    }
}
