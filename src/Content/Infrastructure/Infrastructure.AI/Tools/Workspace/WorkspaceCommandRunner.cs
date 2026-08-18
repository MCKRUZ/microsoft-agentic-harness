using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.AI.Workspace;
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
    /// <param name="scopedServices">
    /// The per-execution DI scope's provider, used to resolve the keyed-scoped
    /// <see cref="ISandboxExecutor"/> for the effective isolation tier — resolved here, after the
    /// profile, rather than passed in already-resolved (#405 follow-up, a security-review finding on
    /// the original fix): the executor must be selected for the tier the operator's
    /// <c>MinimumIsolation</c> override actually resolves to, not a tier fixed before that override
    /// was consulted. Selecting the executor from a caller-fixed tier while the profile silently
    /// carries a different, elevated one is the same "one field, two meanings" defect class #405
    /// exists to close, reproduced on the isolation axis instead of the capability axis.
    /// </param>
    /// <param name="defaultIsolationLevel">
    /// The tool's own minimum isolation requirement, independent of any operator override — the floor
    /// this run never drops below even absent a <c>MinimumIsolation</c> override.
    /// </param>
    /// <param name="toolName">Tool name for diagnostic attribution in the sandbox request, and the
    /// keyed-DI name <paramref name="permissionResolver"/> looks up an operator's per-tool override
    /// under.</param>
    /// <param name="requiredCapabilities">
    /// The sandbox capabilities this run needs — supplied by the caller (e.g.
    /// <c>WorkspaceRunTestsTool.RequiredSandboxCapabilities</c>) rather than hardcoded here, so there
    /// is one place that states what a <c>run_tests</c>/<c>run_lint</c> call may do, not two (#387).
    /// </param>
    /// <param name="permissionResolver">
    /// Resolves the operator's <c>ToolOverrideConfig</c> for <paramref name="toolName"/> — this runner
    /// used to build its permission profile inline, so a per-tool <c>DeniedCapabilities</c> or
    /// <c>MinimumIsolation</c> override never reached it (#405). Via
    /// <see cref="ToolPermissionProfileResolver.ResolveForUngovernedDispatch"/>, which also refuses
    /// outright when the override intersects <paramref name="requiredCapabilities"/>, matching the
    /// governed-call semantics <c>CapabilityEnforcer</c> guarantees rather than silently narrowing
    /// what gets provisioned.
    /// </param>
    /// <param name="logger">
    /// The caller's own logger, used to record a governance refusal before dispatch — the sibling
    /// <c>IacSandboxRunner.MapDispatchFailure</c> logs this same event on the <c>iac_plan</c>/<c>iac_scan</c>
    /// dispatch path; this runner's equivalent refusal used to return silently.
    /// </param>
    /// <param name="timeout">Optional wall-clock timeout for the command. Defaults to 5 minutes — tests can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ToolResult"/> describing the run outcome.</returns>
    public static async Task<ToolResult> RunAsync(
        string commandLine,
        WorkspaceContext workspace,
        IServiceProvider scopedServices,
        SandboxIsolationLevel defaultIsolationLevel,
        string toolName,
        ToolCapability requiredCapabilities,
        ToolPermissionProfileResolver permissionResolver,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopedServices);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(permissionResolver);
        ArgumentNullException.ThrowIfNull(logger);

        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return ToolResult.Fail("Command line is empty.");

        var program = tokens[0];
        var arguments = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        // Profile and executor resolved together — the ordering invariant (executor only after the
        // profile, at its resolved tier) is now structural inside ResolveExecutorForUngovernedDispatch
        // rather than reproduced here, a /simplify finding: this runner and IacSandboxRunner had
        // independently copy-pasted the same resolve-then-select sequence, with only a comment at each
        // site — not a shared implementation — protecting the ordering the stale-tier bug depended on.
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

        SandboxExecutionResult sandboxResult;
        try
        {
            sandboxResult = await executor.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Sandbox execution failed: {ex.GetType().Name}.");
        }

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
