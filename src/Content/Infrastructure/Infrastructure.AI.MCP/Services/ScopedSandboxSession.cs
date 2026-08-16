using Application.AI.Common.Interfaces.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="ISandboxSession"/> so that disposing it also disposes the
/// <see cref="AsyncServiceScope"/> it was resolved from. <see cref="ISandboxSessionFactory"/> is
/// scoped (it depends on <c>ISandboxEgressPreflight</c>, which resolves the ambient agent
/// identity per call), but the singleton <c>McpConnectionManager</c> needs to hold a session
/// well past the lifetime of any request scope — the same pattern the codebase already uses
/// elsewhere for a singleton/scoped-dependency mismatch (see <c>TerraformGenerator</c>'s per-run
/// <c>ISandboxExecutor</c> resolution, which uses this same <c>CreateAsyncScope</c> +
/// <see cref="IAsyncDisposable"/> shape rather than the synchronous <see cref="IServiceScope"/>
/// one). Every member below is a pure delegation except <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ScopedSandboxSession(ISandboxSession inner, AsyncServiceScope scope) : ISandboxSession
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
        // try/finally, not a plain sequence: if the inner session's own teardown throws, the
        // scope must still be released — otherwise a single bad disposal leaks the DI scope (and
        // every scoped disposable it resolved) for the lifetime of the singleton that held it.
        try
        {
            await inner.DisposeAsync();
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }
}
