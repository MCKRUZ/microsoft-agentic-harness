using Domain.AI.Models;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Sandboxed file system operations. Implementations restrict access to configured
/// base paths and block system directories.
/// </summary>
/// <remarks>
/// <para>
/// This is the general-purpose file service — consumed directly by skill loaders,
/// agent parsers, and other non-LLM code paths. The <c>FileSystemTool</c> wraps
/// this service and implements <see cref="ITool"/> for LLM consumption.
/// </para>
/// <para>
/// Implementations must:
/// <list type="bullet">
///   <item>Resolve all paths to absolute and validate against allowed base paths</item>
///   <item>Block writes to system directories (Windows, Program Files, etc.)</item>
///   <item>Enforce maximum file size limits on reads and writes</item>
///   <item>Return normalized forward-slash paths in results</item>
/// </list>
/// </para>
/// </remarks>
public interface IFileSystemService
{
    /// <summary>
    /// Reads the content of a file within the security sandbox.
    /// </summary>
    /// <param name="path">The file path to read (absolute or relative to an allowed base path).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file content as a string.</returns>
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes content to a file within the security sandbox.
    /// Creates parent directories if they don't exist.
    /// </summary>
    /// <param name="path">The file path to write (absolute or relative to an allowed base path).</param>
    /// <param name="content">The content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists files and directories at the given path within the security sandbox.
    /// </summary>
    /// <param name="path">The directory path to list.</param>
    /// <param name="pattern">Optional glob pattern to filter results (e.g., "*.md").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of file and directory paths relative to the listed directory.</returns>
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path, string? pattern = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for files containing the specified term within the security sandbox.
    /// </summary>
    /// <param name="path">The directory to search in.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pattern">Optional glob pattern to filter files (e.g., "*.cs").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching results with file path, snippet, and line number.</returns>
    Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string path, string searchTerm, string? pattern = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a file or directory exists within the security sandbox.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the path exists and is within allowed base paths.</returns>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
}
