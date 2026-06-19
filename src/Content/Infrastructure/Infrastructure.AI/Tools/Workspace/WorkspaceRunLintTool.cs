using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound lint runner. Runs the lint command declared by the active
/// <c>WorkspaceContext</c> inside the sandbox. Same pattern as
/// <see cref="WorkspaceRunTestsTool"/> — the sandbox owns isolation and
/// resource limits; the tool only resolves which command to run.
/// </summary>
/// <remarks>
/// Lint is treated as a separate capability so a workspace can opt in to
/// tests but not lint (or vice versa) without having to fold both into a
/// single command. Both tools expose the same operation name (<c>run</c>)
/// so the agent's mental model stays consistent.
/// </remarks>
public sealed class WorkspaceRunLintTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "run_lint";

    private static readonly IReadOnlyList<string> Operations = ["run"];

    private readonly IWorkspaceContextAccessor _workspace;
    private readonly ISandboxExecutor _sandbox;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceRunLintTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="sandbox">Sandbox executor used to dispatch the lint command in isolation.</param>
    public WorkspaceRunLintTool(IWorkspaceContextAccessor workspace, ISandboxExecutor sandbox)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sandbox);
        _workspace = workspace;
        _sandbox = sandbox;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    public string Description =>
        "Runs the workspace's lint command inside the sandbox. Returns the exit code and combined output.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "run", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: run.");

        var workspace = _workspace.CurrentWorkspace;
        if (workspace is null)
            return ToolResult.Fail("No workspace context is active. run_lint requires the sandbox-injected workspace.");

        if (!workspace.HasLintCommand)
            return ToolResult.Fail("Workspace has no LintCommand configured.");

        return await WorkspaceCommandRunner.RunAsync(
            workspace.LintCommand,
            workspace,
            _sandbox,
            ToolName,
            timeout: null,
            cancellationToken);
    }
}
