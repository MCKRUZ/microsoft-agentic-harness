using Application.AI.Common.Interfaces.Sandbox;
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
/// The permission profile grants <see cref="ToolCapability.FileRead"/>,
/// <see cref="ToolCapability.FileWrite"/> (test runners drop into
/// <c>bin/</c>/<c>obj/</c>), and <see cref="ToolCapability.Subprocess"/>
/// (e.g. <c>dotnet test</c> spawns the test host). It explicitly does NOT
/// grant <see cref="ToolCapability.NetworkAccess"/> — the workspace skill's
/// egress allowlist is empty by design, and the verifier capabilities must
/// match.
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
    /// <param name="toolName">Tool name for diagnostic attribution in the sandbox request.</param>
    /// <param name="timeout">Optional wall-clock timeout for the command. Defaults to 5 minutes — tests can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ToolResult"/> describing the run outcome.</returns>
    public static async Task<ToolResult> RunAsync(
        string commandLine,
        WorkspaceContext workspace,
        ISandboxExecutor executor,
        string toolName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return ToolResult.Fail("Command line is empty.");

        var program = tokens[0];
        var arguments = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities =
                ToolCapability.FileRead
                | ToolCapability.FileWrite
                | ToolCapability.Subprocess,
            AllowedPaths = [workspace.WorkingCopyPath],
            AllowedPrograms = [program],
            AllowedHosts = [],
            DeniedHosts = [],
            DeniedPaths = [],
            MinimumIsolation = SandboxIsolationLevel.Process
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
