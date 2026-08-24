using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.AI.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Shared dispatch helper for <see cref="WorkspaceRunTestsTool"/> and
/// <see cref="WorkspaceRunLintTool"/>. Resolves the effective permission profile, resolves the
/// keyed-scoped <see cref="ISandboxExecutor"/> for the profile's effective isolation tier, builds a
/// <see cref="SandboxExecutionRequest"/> from the workspace's configured command string, runs it, and
/// maps the result to a <see cref="ToolResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Command strings are split on whitespace into program + arguments. The
/// program is the first token; the remaining tokens become
/// <c>ArgumentList</c> entries — the obsolete-error <c>Arguments</c> string
/// surface is intentionally unused so we never expose a shell-injection
/// vector even via the sandbox boundary.
/// </para>
/// <para>
/// The permission profile grants whatever <see cref="ToolCapability"/> the caller declares — see
/// <c>WorkspaceRunTestsTool.RequiredSandboxCapabilities</c>/<c>WorkspaceRunLintTool.RequiredSandboxCapabilities</c>,
/// the single source of truth this runner used to duplicate as a hardcoded literal (#387). Neither
/// declares <see cref="ToolCapability.NetworkAccess"/> — the workspace skill's egress allowlist is
/// empty by design, and the verifier capabilities must match.
/// </para>
/// </remarks>
public static class WorkspaceCommandRunner
{
    /// <summary>
    /// Runs <paramref name="commandLine"/> inside the sandbox. Returns a <see cref="ToolResult"/>
    /// that includes stdout/stderr and the exit code.
    /// </summary>
    /// <param name="commandLine">The whitespace-delimited command line. First token is the program; remaining tokens are arguments.</param>
    /// <param name="workspace">
    /// The active workspace context. Not itself an enforced filesystem boundary on this dispatch
    /// path — the profile's old <c>AllowedPaths</c> was removed as dead config (#405); the caller
    /// (<c>WorkspaceRunTestsTool</c>/<c>WorkspaceRunLintTool</c>) reads the command to run from it
    /// before calling here.
    /// </param>
    /// <param name="scopeFactory">
    /// The caller's <see cref="IServiceScopeFactory"/>. This method opens one fresh scope per run — so
    /// the caller's own singleton tool never captures scope-bound state — and resolves both the
    /// <see cref="ToolPermissionProfileResolver"/> and the keyed-scoped <see cref="ISandboxExecutor"/>
    /// for the effective isolation tier from it, entirely inside the try/catch below (#426: the two
    /// callers used to create this scope and resolve the resolver themselves, outside any try/catch,
    /// mirroring the exact gap #421 fixed for <c>IacSandboxRunner</c>/<c>TerraformGenerator</c>/
    /// <c>BicepGenerator</c> — a DI-resolution or executor-lookup failure threw uncaught out of
    /// <c>ITool.ExecuteAsync</c>). The executor is resolved after the profile (#405 follow-up, a
    /// security-review finding mirroring the identical fix in <c>IacSandboxRunner</c>): it must be
    /// selected for the tier the operator's <c>MinimumIsolation</c> override actually resolves to, not
    /// a tier fixed before that override was consulted.
    /// </param>
    /// <param name="defaultIsolationLevel">
    /// The tool's own minimum isolation requirement, independent of any operator override — the floor
    /// this run never drops below even absent a <c>MinimumIsolation</c> override.
    /// </param>
    /// <param name="toolName">Tool name for diagnostic attribution in the sandbox request, and the
    /// keyed-DI name the resolved <see cref="ToolPermissionProfileResolver"/> looks up an operator's
    /// per-tool override under.</param>
    /// <param name="requiredCapabilities">
    /// The sandbox capabilities this run needs — supplied by the caller (e.g.
    /// <c>WorkspaceRunTestsTool.RequiredSandboxCapabilities</c>) rather than hardcoded here, so there
    /// is one place that states what a <c>run_tests</c>/<c>run_lint</c> call may do, not two (#387).
    /// </param>
    /// <param name="logger">
    /// The caller's own logger — used both to record a governance refusal before dispatch and to log a
    /// sandbox-level exception here directly rather than in each caller's own try/catch. The sibling
    /// <c>IacSandboxRunner.RunAsync</c> logs the equivalent event on the <c>iac_plan</c>/<c>iac_scan</c>
    /// dispatch path.
    /// </param>
    /// <param name="timeout">Optional wall-clock timeout for the command. Defaults to 5 minutes — tests can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ToolResult"/> describing the run outcome.</returns>
    public static async Task<ToolResult> RunAsync(
        string commandLine,
        WorkspaceContext workspace,
        IServiceScopeFactory scopeFactory,
        SandboxIsolationLevel defaultIsolationLevel,
        string toolName,
        ToolCapability requiredCapabilities,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(logger);

        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return ToolResult.Fail("Command line is empty.");

        var program = tokens[0];
        var arguments = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        try
        {
            // Scope creation, ToolPermissionProfileResolver resolution, dispatch resolution, and
            // execution all live inside this one try/catch (#426) — mirrors IacSandboxRunner.RunAsync's
            // final shape after #421's three successive widenings.
            await using var scope = scopeFactory.CreateAsyncScope();
            var permissionResolver = scope.ServiceProvider.GetRequiredService<ToolPermissionProfileResolver>();

            return await DispatchAsync(
                program, arguments, workspace, scope.ServiceProvider, defaultIsolationLevel,
                toolName, requiredCapabilities, permissionResolver, logger, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ToolName} sandbox run failed for {WorkingCopyPath}.", toolName, workspace.WorkingCopyPath);
            return ToolResult.Fail($"Sandbox execution failed: {ex.GetType().Name}.");
        }
    }

    private static async Task<ToolResult> DispatchAsync(
        string program,
        string[] arguments,
        WorkspaceContext workspace,
        IServiceProvider scopedServices,
        SandboxIsolationLevel defaultIsolationLevel,
        string toolName,
        ToolCapability requiredCapabilities,
        ToolPermissionProfileResolver permissionResolver,
        ILogger logger,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        // Profile and executor resolved together — the ordering invariant (executor only after the
        // profile, at its resolved tier) is structural inside ResolveExecutorForUngovernedDispatch
        // rather than reproduced here, a /simplify finding: this runner and IacSandboxRunner had
        // independently copy-pasted the same resolve-then-select sequence, with only a comment at
        // each site — not a shared implementation — protecting the ordering the stale-tier bug
        // depended on.
        var dispatchResult = permissionResolver.ResolveExecutorForUngovernedDispatch(
            toolName, requiredCapabilities, [program], scopedServices, defaultIsolationLevel);
        if (!dispatchResult.IsSuccess)
        {
            var reason = string.Join("; ", dispatchResult.Errors);
            logger.LogError(
                "{ToolName} for {WorkingCopyPath} was refused before dispatch: {Reason}",
                toolName, workspace.WorkingCopyPath, reason);
            return ToolResult.Fail(reason);
        }

        var (profile, executor) = dispatchResult.Value!;

        var request = new SandboxExecutionRequest
        {
            ToolName = toolName,
            Input = string.Empty,
            Command = program,
            ArgumentList = arguments,
            Limits = new ResourceLimits(),
            PermissionProfile = profile,
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };

        // ExecuteNonNullAsync guards a custom ISandboxExecutor violating its non-nullable contract —
        // without it, sandboxResult.ExitCode/.Success below was an unguarded NullReferenceException
        // caught only by RunAsync's outer catch, degrading to a generic message instead of the
        // specific, stable-error-code failure every other dispatch-time fault in this method returns.
        var sandboxResult = await executor.ExecuteNonNullAsync(request, cancellationToken).ConfigureAwait(false);

        var summary =
            $"exit={sandboxResult.ExitCode?.ToString() ?? "n/a"} success={sandboxResult.Success}\n" +
            (sandboxResult.Output ?? string.Empty);

        return sandboxResult.Success
            ? ToolResult.Ok(summary)
            : ToolResult.Fail(
                $"{toolName} failed (exit={sandboxResult.ExitCode?.ToString() ?? "n/a"}): " +
                $"{sandboxResult.ErrorMessage ?? sandboxResult.Output ?? "no output"}");
    }
}
