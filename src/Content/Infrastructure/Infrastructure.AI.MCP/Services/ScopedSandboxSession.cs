using Application.AI.Common.Interfaces.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="ISandboxSession"/> so that disposing it also disposes the
/// <see cref="AsyncServiceScope"/> it was resolved from. <see cref="ISandboxSessionFactory"/> is
/// scoped (it depends on <c>ISandboxEgressPreflight</c>, which resolves the ambient agent
/// identity per call), but the singleton <c>McpConnectionManager</c> needs to hold a session well
/// past the lifetime of any request scope. Unlike <c>TerraformGenerator</c>'s per-run
/// <c>ISandboxExecutor</c> resolution — which also uses <c>CreateAsyncScope</c>, but disposes the
/// scope with <c>await using</c> at the end of the same method, because a one-shot execution
/// finishes inside the scope that started it — a session outlives the method that created it, so
/// ownership of the scope has to transfer to something that outlives it too. This type is that:
/// the first genuine scope-outlives-its-creating-method case in this codebase, not a copy of an
/// existing one. Every member below is a pure delegation except <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ScopedSandboxSession(ISandboxSession inner, AsyncServiceScope scope, Action? onDisposed = null) : ISandboxSession
{
    /// <inheritdoc />
    public Stream StandardInput => inner.StandardInput;

    /// <inheritdoc />
    public Stream StandardOutput => inner.StandardOutput;

    /// <inheritdoc />
    public Task Completion => inner.Completion;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Nested try/finally, not a plain sequence: if the inner session's own teardown throws, the
        // scope must still be released — otherwise a single bad disposal leaks the DI scope (and
        // every scoped disposable it resolved) for the lifetime of the singleton that held it. The
        // outer finally runs regardless of either inner failure — see onDisposed's own remarks for
        // why it must fire even when teardown itself throws.
        try
        {
            try
            {
                await inner.DisposeAsync();
            }
            finally
            {
                await scope.DisposeAsync();
            }
        }
        finally
        {
            // Releases a slot the caller reserved BEFORE this session was ever handed back to it
            // (see McpConnectionManager.StartSandboxedStdioSessionAsync's host-wide concurrency
            // cap) — must fire even when the disposal above throws, or a session whose own
            // teardown failed would permanently pin its slot for the rest of the host process's
            // life, eventually starving every other bundle of the sandbox capability entirely.
            onDisposed?.Invoke();
        }
    }
}
