using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;

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
/// <para>
/// The tool is a keyed SINGLETON but <see cref="ISandboxExecutor"/> is keyed
/// SCOPED, so the executor is resolved per execution from a fresh DI scope
/// (via <see cref="IServiceScopeFactory"/>) instead of being captured at
/// construction — a captured executor would be a captive dependency that
/// scope validation rejects and that shares scoped state across requests.
/// </para>
/// </remarks>
public sealed class WorkspaceRunTestsTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "run_tests";

    private static readonly IReadOnlyList<string> Operations = ["run"];

    private readonly IWorkspaceContextAccessor _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SandboxIsolationLevel _isolationLevel;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceRunTestsTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="scopeFactory">Scope factory used to resolve the scoped sandbox executor per execution.</param>
    /// <param name="isolationLevel">The sandbox isolation level to resolve the executor for. Defaults to <see cref="SandboxIsolationLevel.Process"/>.</param>
    public WorkspaceRunTestsTool(
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
    public BlastRadius RiskTier => BlastRadius.Medium;

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

        // The executor is SCOPED — resolve it from a fresh scope per execution
        // so this singleton tool never captures scope-bound state.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sandbox = scope.ServiceProvider.GetRequiredKeyedService<ISandboxExecutor>(_isolationLevel);

        return await WorkspaceCommandRunner.RunAsync(
            workspace.TestCommand,
            workspace,
            sandbox,
            ToolName,
            timeout: null,
            cancellationToken);
    }
}
