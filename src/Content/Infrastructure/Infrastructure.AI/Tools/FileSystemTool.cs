using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Adapts <see cref="IFileSystemService"/> as an <see cref="ITool"/> for LLM consumption.
/// Dispatches JSON parameters from the LLM to the appropriate service method and
/// returns results as strings for the conversation.
/// </summary>
/// <remarks>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;("file_system", (sp, _) =&gt;
///     new FileSystemTool(sp.GetRequiredService&lt;IFileSystemService&gt;()));
/// </code>
/// </para>
/// <para>
/// The <see cref="IToolConverter"/> creates an <c>AIFunction</c> from this tool,
/// with the parameter class driving JSON Schema generation for the LLM.
/// </para>
/// </remarks>
public sealed class FileSystemTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "file_system";

    private static readonly IReadOnlyList<string> Operations =
        ["read", "write", "list", "search", "exists"];

    private readonly IFileSystemService _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemTool"/> class.
    /// </summary>
    /// <param name="fileSystem">The sandboxed file system service.</param>
    public FileSystemTool(IFileSystemService fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description => "Reads, writes, lists, and searches files within the project sandbox.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return operation.ToLowerInvariant() switch
            {
                "read" => await ReadAsync(parameters, cancellationToken),
                "write" => await WriteAsync(parameters, cancellationToken),
                "list" => await ListAsync(parameters, cancellationToken),
                "search" => await SearchAsync(parameters, cancellationToken),
                "exists" => await ExistsAsync(parameters, cancellationToken),
                _ => ToolResult.Fail($"Unknown operation: {operation}. Supported: {string.Join(", ", Operations)}")
            };
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail("Access denied: path is outside the allowed sandbox.");
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
            return ToolResult.Fail("I/O error: file may exceed size limits or be inaccessible.");
        }
        catch (ArgumentException)
        {
            return ToolResult.Fail("Invalid path or parameters.");
        }
    }

    private async Task<ToolResult> ReadAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var path = GetRequiredString(parameters, "path");
        var content = await _fileSystem.ReadFileAsync(path, ct);
        return ToolResult.Ok(content);
    }

    private async Task<ToolResult> WriteAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var path = GetRequiredString(parameters, "path");
        var content = GetRequiredString(parameters, "content");
        await _fileSystem.WriteFileAsync(path, content, ct);
        return ToolResult.Ok($"Written {content.Length} characters to {path}");
    }

    private async Task<ToolResult> ListAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var path = GetRequiredString(parameters, "path");
        var pattern = GetOptionalString(parameters, "pattern");
        var entries = await _fileSystem.ListDirectoryAsync(path, pattern, ct);
        return ToolResult.Ok(string.Join('\n', entries));
    }

    private async Task<ToolResult> SearchAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var path = GetRequiredString(parameters, "path");
        var searchTerm = GetRequiredString(parameters, "search_term");
        var pattern = GetOptionalString(parameters, "pattern");
        var results = await _fileSystem.SearchFilesAsync(path, searchTerm, pattern, ct);

        if (results.Count == 0)
            return ToolResult.Ok("No matches found.");

        var lines = results.Select(r =>
            r.LineNumber.HasValue
                ? $"{r.FilePath}:{r.LineNumber}: {r.Snippet}"
                : $"{r.FilePath}: {r.Snippet}");

        return ToolResult.Ok(string.Join('\n', lines));
    }

    private async Task<ToolResult> ExistsAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var path = GetRequiredString(parameters, "path");
        var exists = await _fileSystem.ExistsAsync(path, ct);
        return ToolResult.Ok(exists ? "true" : "false");
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is not string s || string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
        return s;
    }

    private static string? GetOptionalString(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;
}
