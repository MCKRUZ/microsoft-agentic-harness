using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound read tool. Reads a file from the sandbox-injected working
/// copy and returns its contents to the agent. Read-only and concurrency-safe.
/// </summary>
/// <remarks>
/// <para>
/// The agent supplies a path relative to the working copy (or an absolute path
/// that still resolves inside it). <see cref="WorkspacePathResolver"/> rejects
/// any path that escapes; the tool surfaces a generic refusal so host
/// filesystem layout never leaks into LLM context.
/// </para>
/// <para>
/// When no workspace scope is active (the ambient is null) the tool refuses —
/// the sandbox-required guarantee on the workspace skill depends on this.
/// </para>
/// </remarks>
public sealed class WorkspaceReadFileTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "read_file";

    private static readonly IReadOnlyList<string> Operations = ["read"];

    private readonly IWorkspaceContextAccessor _workspace;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceReadFileTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    public WorkspaceReadFileTool(IWorkspaceContextAccessor workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Reads a file from the sandbox-injected workspace. Path is relative to the working copy. Read-only.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "read", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: read.");

        var workspace = _workspace.CurrentWorkspace;
        if (workspace is null)
            return ToolResult.Fail("No workspace context is active. read_file requires the sandbox-injected workspace.");

        if (!parameters.TryGetValue("path", out var pathValue) || pathValue is not string path || string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("Required parameter 'path' is missing or empty.");

        try
        {
            var fullPath = WorkspacePathResolver.Resolve(workspace, path);
            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail("Access denied: path is outside the workspace.");
        }
        catch (FileNotFoundException)
        {
            return ToolResult.Fail("File not found.");
        }
        catch (DirectoryNotFoundException)
        {
            return ToolResult.Fail("Directory not found.");
        }
        catch (IOException)
        {
            return ToolResult.Fail("I/O error while reading the file.");
        }
        catch (ArgumentException)
        {
            return ToolResult.Fail("Invalid path.");
        }
    }
}
