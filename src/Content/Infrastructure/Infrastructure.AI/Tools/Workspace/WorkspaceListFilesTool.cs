using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound directory listing tool. Returns the entries under a
/// directory in the sandbox-injected working copy, optionally filtered by a
/// glob pattern. Read-only and concurrency-safe.
/// </summary>
/// <remarks>
/// <para>
/// Listing is non-recursive by default to keep output bounded — agents that
/// need deep traversal can pass <c>recursive=true</c>. Output is one entry per
/// line, paths relative to the working copy with forward-slash separators.
/// </para>
/// <para>
/// The path argument is resolved by <see cref="WorkspacePathResolver"/>; any
/// escape attempt returns a generic refusal. A null ambient workspace context
/// is a hard refuse.
/// </para>
/// </remarks>
public sealed class WorkspaceListFilesTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "list_files";

    private static readonly IReadOnlyList<string> Operations = ["list"];

    private readonly IWorkspaceContextAccessor _workspace;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceListFilesTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    public WorkspaceListFilesTool(IWorkspaceContextAccessor workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Lists files in a directory inside the sandbox-injected workspace. Supports optional glob pattern and recursive listing.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "list", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail($"Unknown operation: {operation}. Supported: list."));

        var workspace = _workspace.CurrentWorkspace;
        if (workspace is null)
            return Task.FromResult(ToolResult.Fail(
                "No workspace context is active. list_files requires the sandbox-injected workspace."));

        if (!parameters.TryGetValue("path", out var pathValue) || pathValue is not string path || string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("Required parameter 'path' is missing or empty."));

        var pattern = parameters.TryGetValue("pattern", out var patternValue) && patternValue is string p && !string.IsNullOrWhiteSpace(p)
            ? p
            : "*";

        var recursive = parameters.TryGetValue("recursive", out var recursiveValue) && recursiveValue is bool r && r;

        try
        {
            var fullPath = WorkspacePathResolver.Resolve(workspace, path);
            if (!Directory.Exists(fullPath))
                return Task.FromResult(ToolResult.Fail("Directory not found."));

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = Directory.EnumerateFileSystemEntries(fullPath, pattern, searchOption)
                .Select(e => WorkspacePathResolver.ToRelative(workspace, e))
                .OrderBy(s => s, StringComparer.Ordinal);

            return Task.FromResult(ToolResult.Ok(string.Join('\n', entries)));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Fail("Access denied: path is outside the workspace."));
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(ToolResult.Fail("Directory not found."));
        }
        catch (IOException)
        {
            return Task.FromResult(ToolResult.Fail("I/O error while listing the directory."));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ToolResult.Fail("Invalid path or glob pattern."));
        }
    }
}
