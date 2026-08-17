using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.AI.Workspace;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Shared dispatch helper for <see cref="WorkspaceRunTestsTool"/> and
/// <see cref="WorkspaceRunLintTool"/>. Builds a
/// <see cref="SandboxExecutionRequest"/> from the workspace's configured
/// command string, runs it through the supplied
/// <see cref="ISandboxExecutor"/>, and maps the result to a
/// <see cref="ToolResult"/>.
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
    /// Runs <paramref name="commandLine"/> inside the sandbox at the
    /// workspace's working copy. Returns a <see cref="ToolResult"/> that
    /// includes stdout/stderr and the exit code.
    /// </summary>
    /// <param name="commandLine">The whitespace-delimited command line. First token is the program; remaining tokens are arguments.</param>
    /// <param name="workspace">The active workspace context — supplies the working copy path the sandbox roots its capabilities to.</param>
    /// <param name="executor">The sandbox executor to dispatch through.</param>
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
    /// <c>MinimumIsolation</c> override never reached it (#405). Only the override's
    /// <c>DeniedCapabilities</c> and <c>MinimumIsolation</c> are taken; <paramref name="requiredCapabilities"/>
    /// and the workspace-derived <c>AllowedPrograms</c> below stay caller-supplied — the resolver's base
    /// declaration would be redundant with what the caller already knows, and merging isolation must
    /// never downgrade below <see cref="SandboxIsolationLevel.Process"/>, the floor this runner requires.
    /// </param>
    /// <param name="timeout">Optional wall-clock timeout for the command. Defaults to 5 minutes — tests can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ToolResult"/> describing the run outcome.</returns>
    public static async Task<ToolResult> RunAsync(
        string commandLine,
        WorkspaceContext workspace,
        ISandboxExecutor executor,
        string toolName,
        ToolCapability requiredCapabilities,
        ToolPermissionProfileResolver permissionResolver,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(permissionResolver);

        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return ToolResult.Fail("Command line is empty.");

        var program = tokens[0];
        var arguments = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        var overridden = permissionResolver.Resolve(toolName);
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = requiredCapabilities,
            DeniedCapabilities = overridden.DeniedCapabilities,
            AllowedPrograms = [program],
            MinimumIsolation = (SandboxIsolationLevel)Math.Max(
                (int)SandboxIsolationLevel.Process, (int)overridden.MinimumIsolation)
        };

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
