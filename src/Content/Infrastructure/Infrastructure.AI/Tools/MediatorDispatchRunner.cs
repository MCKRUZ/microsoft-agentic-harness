using Domain.AI.Models;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Shared dispatch wrapper for <c>ITool</c> implementations that submit a MediatR command from a
/// fresh DI scope (as opposed to <c>WorkspaceCommandRunner</c>/<c>IacSandboxRunner</c>, which
/// dispatch to a keyed-scoped <c>ISandboxExecutor</c> instead). Resolves <see cref="IMediator"/>
/// from a new scope, runs the caller's dispatch delegate, and maps any exception to a failed
/// <see cref="ToolResult"/> instead of letting it escape <c>ITool.ExecuteAsync</c> uncaught.
/// </summary>
/// <remarks>
/// Extracted from <c>WorkspaceWriteFileTool</c> and <c>DocumentIngestTool</c> (#428): both tools
/// independently duplicated this exact scope-creation/resolve/dispatch/catch shape after fixing the
/// uncaught-exception gap #421 (<c>IacSandboxRunner</c>) and #426 (<c>WorkspaceCommandRunner</c>)
/// already closed on the sandbox-dispatch path — the same "fix one instance, stop" defect shape
/// CLAUDE.md's own Common Mistakes section records for that history.
/// </remarks>
internal static class MediatorDispatchRunner
{
    /// <summary>
    /// Runs <paramref name="dispatch"/> against an <see cref="IMediator"/> resolved from a fresh scope.
    /// </summary>
    /// <param name="scopeFactory">
    /// The caller's <see cref="IServiceScopeFactory"/>. A fresh scope is created per call so the
    /// caller's own singleton tool never captures scope-bound state, and so the MediatR pipeline can
    /// resolve scoped services (e.g. <c>IAgentExecutionContext</c>) that a root-bound mediator would
    /// reject as a captive dependency.
    /// </param>
    /// <param name="dispatch">Builds the command and sends it via the scoped <see cref="IMediator"/>.</param>
    /// <param name="logger">Logs a scope-creation, resolution, or dispatch failure before it is mapped.</param>
    /// <param name="toolName">Tool name for the log template and the failure message prefix.</param>
    /// <param name="failureContext">
    /// Free-form context (e.g. a path or URI) included in the log entry <strong>verbatim</strong> —
    /// callers own scrubbing anything credential-bearing (query strings, userinfo) out of this value
    /// before passing it; this method does not inspect or redact it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <paramref name="dispatch"/>'s result, or a failed <see cref="ToolResult"/> whose message is
    /// scrubbed to the exception's type name — never <c>ex.Message</c>, which can carry raw exception
    /// text (connection strings, stack detail) that must not reach the model or an audit log verbatim.
    /// </returns>
    public static async Task<ToolResult> RunAsync(
        IServiceScopeFactory scopeFactory,
        Func<IMediator, CancellationToken, Task<ToolResult>> dispatch,
        ILogger logger,
        string toolName,
        string failureContext,
        CancellationToken cancellationToken)
    {
        // Scope creation lives outside the try/finally pair below so an already-obtained successful
        // `result` is never discarded: disposing the scope AFTER the dispatch has committed its write
        // can itself throw, and if that were inside the same try/catch that maps dispatch failures,
        // a genuinely successful ChangeProposal submission or ingest would be reported to the model as
        // "dispatch failed" — inviting a retry of work that already landed.
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await dispatch(mediator, cancellationToken).ConfigureAwait(false);
            await DisposeScopeAsync(scope, logger, toolName).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only rethrow when it's genuinely the caller's own token — an internal timeout unrelated
            // to the caller (e.g. HttpClient's own timeout mid-fetch) also throws OperationCanceledException
            // but must be mapped to a failure below, not escape ExecuteAsync uncaught (#428's own gap,
            // mirroring the caller-token guard RestrictedSearchTool.cs already uses for the same reason).
            await DisposeScopeAsync(scope, logger, toolName).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ToolName} dispatch failed for {FailureContext}.", toolName, failureContext);
            await DisposeScopeAsync(scope, logger, toolName).ConfigureAwait(false);
            return ToolResult.Fail($"{toolName} dispatch failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Disposes <paramref name="scope"/>, logging (not throwing) if disposal itself fails. A disposal
    /// failure is a resource-cleanup problem worth surfacing in logs, but must never overwrite a
    /// dispatch outcome — success or failure — that was already determined before disposal ran.
    /// </summary>
    private static async Task DisposeScopeAsync(AsyncServiceScope scope, ILogger logger, string toolName)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ToolName} DI scope disposal failed after dispatch completed.", toolName);
        }
    }
}
