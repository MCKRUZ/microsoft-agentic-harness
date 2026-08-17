using Application.AI.Common.Interfaces.Sandbox;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="ISandboxSession"/> so that disposing it also releases a caller-held
/// concurrency slot — <c>McpConnectionManager.StartSandboxedStdioSessionAsync</c>'s host-wide cap on
/// live bundle-owned sandboxed sessions.
/// </summary>
/// <remarks>
/// A separate decorator, composed with <see cref="ScopedSandboxSession"/> rather than folded into it
/// (an earlier version threaded an <c>onDisposed</c> callback through <c>ScopedSandboxSession</c>'s own
/// constructor instead — a /simplify altitude finding caught it as smuggling a second, unrelated
/// disposal responsibility into a type whose own doc comment promises "every member is a pure
/// delegation except <c>DisposeAsync</c>", which itself was scoped to exactly one job: releasing the
/// DI scope. Composition keeps each decorator responsible for exactly one lifetime.
/// </remarks>
public sealed class SlotReleasingSandboxSession(ISandboxSession inner, Action releaseSlot) : ISandboxSession
{
    /// <summary>
    /// Guards <see cref="DisposeAsync"/> against running twice on the same instance — two callers
    /// racing to tear down the same session (e.g. an explicit close overlapping host shutdown
    /// enumeration) must decrement the host-wide concurrency counter exactly once, or the second,
    /// redundant decrement would silently admit one more concurrent session than
    /// <c>MaxConcurrentSessions</c> permits for as long as the host runs — the opposite failure
    /// mode from a leaked slot, but just as real: found by /code-review (#371).
    /// </summary>
    private int _disposed;

    /// <inheritdoc />
    public Stream StandardInput => inner.StandardInput;

    /// <inheritdoc />
    public Stream StandardOutput => inner.StandardOutput;

    /// <inheritdoc />
    public Task Completion => inner.Completion;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // finally, not a plain sequence: the slot must be released even if the inner session's own
        // teardown throws — otherwise a session whose disposal failed would permanently pin its slot
        // for the rest of the host process's life, eventually starving every other bundle of the
        // sandbox capability entirely.
        try
        {
            await inner.DisposeAsync();
        }
        finally
        {
            releaseSlot();
        }
    }
}
