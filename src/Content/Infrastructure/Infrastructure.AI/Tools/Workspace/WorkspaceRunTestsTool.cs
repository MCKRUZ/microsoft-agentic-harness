using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound test runner. Runs the test command declared by the active
/// <c>WorkspaceContext</c> inside the sandbox. Surfaces stdout/stderr and
/// exit code to the agent so it can decide whether the proposed change is
/// safe enough to ask for approval.
/// </summary>
/// <remarks>
/// <para>
/// The command line is resolved from <c>WorkspaceContext.TestCommand</c> at
/// invocation time so consumers can vary it per environment without
/// rewiring the tool. The sandbox enforces the resource limits +
/// capability profile — the tool itself never spawns processes directly.
/// </para>
/// </remarks>
public sealed class WorkspaceRunTestsTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "run_tests";

    private static readonly IReadOnlyList<string> Operations = ["run"];

    private readonly IWorkspaceContextAccessor _workspace;
    private readonly ISandboxExecutor _sandbox;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceRunTestsTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="sandbox">Sandbox executor used to dispatch the test command in isolation.</param>
    public WorkspaceRunTestsTool(IWorkspaceContextAccessor workspace, ISandboxExecutor sandbox)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sandbox);
        _workspace = workspace;
        _sandbox = sandbox;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Runs the workspace's test command inside the sandbox. Returns the exit code and combined output.";

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
            return ToolResult.Fail("No workspace context is active. run_tests requires the sandbox-injected workspace.");

        if (!workspace.HasTestCommand)
            return ToolResult.Fail("Workspace has no TestCommand configured.");

        return await WorkspaceCommandRunner.RunAsync(
            workspace.TestCommand,
            workspace,
            _sandbox,
            ToolName,
            timeout: null,
            cancellationToken);
    }
}
