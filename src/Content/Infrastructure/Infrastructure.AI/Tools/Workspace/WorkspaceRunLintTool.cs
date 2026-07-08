using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound lint runner. Runs the lint command declared by the active
/// <c>WorkspaceContext</c> inside the sandbox. Same pattern as
/// <see cref="WorkspaceRunTestsTool"/> — the sandbox owns isolation and
/// resource limits; the tool only resolves which command to run.
/// </summary>
/// <remarks>
/// <para>
/// Lint is treated as a separate capability so a workspace can opt in to
/// tests but not lint (or vice versa) without having to fold both into a
/// single command. Both tools expose the same operation name (<c>run</c>)
/// so the agent's mental model stays consistent.
/// </para>
/// <para>
/// The tool is a keyed SINGLETON but <see cref="ISandboxExecutor"/> is keyed
/// SCOPED, so the executor is resolved per execution from a fresh DI scope
/// (via <see cref="IServiceScopeFactory"/>) instead of being captured at
/// construction — a captured executor would be a captive dependency that
/// scope validation rejects and that shares scoped state across requests.
/// </para>
/// </remarks>
public sealed class WorkspaceRunLintTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "run_lint";

    private static readonly IReadOnlyList<string> Operations = ["run"];

    private readonly IWorkspaceContextAccessor _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SandboxIsolationLevel _isolationLevel;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceRunLintTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="scopeFactory">Scope factory used to resolve the scoped sandbox executor per execution.</param>
    /// <param name="isolationLevel">The sandbox isolation level to resolve the executor for. Defaults to <see cref="SandboxIsolationLevel.Process"/>.</param>
    public WorkspaceRunLintTool(
        IWorkspaceContextAccessor workspace,
        IServiceScopeFactory scopeFactory,
        SandboxIsolationLevel isolationLevel = SandboxIsolationLevel.Process)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _isolationLevel = isolationLevel;
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

        // The executor is SCOPED — resolve it from a fresh scope per execution
        // so this singleton tool never captures scope-bound state.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sandbox = scope.ServiceProvider.GetRequiredKeyedService<ISandboxExecutor>(_isolationLevel);

        return await WorkspaceCommandRunner.RunAsync(
            workspace.LintCommand,
            workspace,
            sandbox,
            ToolName,
            timeout: null,
            cancellationToken);
    }
}
