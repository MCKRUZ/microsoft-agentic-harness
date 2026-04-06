using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.Common.Extensions;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Sandboxed file system operations restricted to configured base paths.
/// Blocks access to system directories, resolves symlinks, and enforces file size limits.
/// </summary>
/// <remarks>
/// Consumed directly by skill loaders, agent parsers, and other non-LLM code paths.
/// For LLM tool consumption, <see cref="FileSystemTool"/> wraps this service.
/// </remarks>
public sealed class FileSystemService : IFileSystemService
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxSearchResults = 100;
    private const int MaxFilesScanned = 1_000;
    private const int SnippetMaxLength = 200;

    private static readonly HashSet<string> SystemDirectoryBlocklist = BuildSystemBlocklist();

    private readonly ILogger<FileSystemService> _logger;
    private readonly HashSet<string> _allowedBasePaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemService"/> class.
    /// </summary>
    /// <param name="logger">Logger for file operation auditing.</param>
    /// <param name="allowedBasePaths">
    /// The set of absolute directory paths the service is allowed to access.
    /// Paths are normalized and compared case-insensitively. The caller must
    /// explicitly include the working directory if development access is desired.
    /// </param>
    public FileSystemService(
        ILogger<FileSystemService> logger,
        IEnumerable<string> allowedBasePaths)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(allowedBasePaths);

        _logger = logger;
        _allowedBasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in allowedBasePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var fullPath = Path.GetFullPath(path);
                _allowedBasePaths.Add(fullPath);
            }
        }

        if (_allowedBasePaths.Count == 0)
            _logger.LogWarning("FileSystemService initialized with zero allowed base paths — all operations will be denied");
        else
            _logger.LogInformation("FileSystemService initialized with {PathCount} allowed base paths", _allowedBasePaths.Count);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveAndValidate(path);

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"File not found: {path}");

        if (fileInfo.Length > MaxFileSizeBytes)
            throw new IOException($"File exceeds size limit ({MaxFileSizeBytes / 1024 / 1024} MB).");

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length > MaxFileSizeBytes)
            throw new IOException($"Content exceeds size limit ({MaxFileSizeBytes / 1024 / 1024} MB).");

        var fullPath = ResolveAndValidate(path, write: true);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        _logger.LogDebug("Wrote {Length} chars to file", content.Length);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDirectoryAsync(string path, string? pattern = null, CancellationToken cancellationToken = default)
    {
        ValidatePattern(pattern);
        var fullPath = ResolveAndValidate(path);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<string>();
        var searchPattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;

        if (string.IsNullOrEmpty(pattern))
        {
            foreach (var dir in Directory.GetDirectories(fullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(Path.GetFileName(dir) + '/');
            }
        }

        foreach (var file in Directory.GetFiles(fullPath, searchPattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Path.GetFileName(file));
        }

        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(
        string path, string searchTerm, string? pattern = null, CancellationToken cancellationToken = default)
    {
        ValidatePattern(pattern);
        var fullPath = ResolveAndValidate(path);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<FileSearchResult>();
        var searchPattern = string.IsNullOrEmpty(pattern) ? "*.*" : pattern;
        var filesScanned = 0;

        foreach (var file in Directory.EnumerateFiles(fullPath, searchPattern, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++filesScanned > MaxFilesScanned)
            {
                _logger.LogWarning("Search scan limit reached ({Limit} files)", MaxFilesScanned);
                break;
            }

            if (results.Count >= MaxSearchResults)
                break;

            await SearchFileAsync(file, fullPath, searchTerm, results, cancellationToken);
        }

        _logger.LogDebug("Search complete: {ResultCount} results from {ScannedCount} files scanned", results.Count, filesScanned);
        return results;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = ResolveAndValidate(path);
            return Task.FromResult(File.Exists(fullPath) || Directory.Exists(fullPath));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    private async Task SearchFileAsync(
        string filePath, string basePath, string searchTerm,
        List<FileSearchResult> results, CancellationToken cancellationToken)
    {
        try
        {
            var lineNumber = 0;
            await foreach (var line in File.ReadLinesAsync(filePath, cancellationToken))
            {
                lineNumber++;

                if (results.Count >= MaxSearchResults)
                    return;

                if (line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new FileSearchResult
                    {
                        FilePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/'),
                        Snippet = line.Trim().Truncate(SnippetMaxLength),
                        LineNumber = lineNumber
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Skipped file during search: {File}", filePath);
        }
    }

    /// <summary>
    /// Resolves a user-supplied path to an absolute path, validates against the
    /// security sandbox (input validation, allowlist, symlink resolution, write blocklist).
    /// </summary>
    private string ResolveAndValidate(string path, bool write = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Defense-in-depth: reject traversal patterns, null bytes, shell injection
        if (!SecureInputValidatorHelper.ValidateFilePath(path))
            throw new ArgumentException("Path contains invalid characters or traversal patterns.");

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : ResolveRelative(path);

        // Resolve symlinks/junctions to real target, then re-validate
        fullPath = ResolveSymlinks(fullPath);

        if (!IsPathAllowed(fullPath))
        {
            _logger.LogWarning("Blocked access to path outside sandbox: {Path}", fullPath);
            throw new UnauthorizedAccessException("Path is outside the allowed sandbox.");
        }

        if (write)
            ValidateWriteTarget(fullPath);

        return fullPath;
    }

    private string ResolveRelative(string path)
    {
        foreach (var basePath in _allowedBasePaths)
        {
            var combined = Path.GetFullPath(Path.Combine(basePath, path));
            if (IsPathAllowed(combined) && (File.Exists(combined) || Directory.Exists(combined)))
                return combined;
        }

        // Default to first allowed base path (caller configured these explicitly)
        return _allowedBasePaths.Count > 0
            ? Path.GetFullPath(Path.Combine(_allowedBasePaths.First(), path))
            : throw new UnauthorizedAccessException("No allowed base paths configured.");
    }

    private static string ResolveSymlinks(string path)
    {
        // Check the file itself for symlink
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            return Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(path)!);

        // Walk parent directories checking for junction points
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (dir is not null)
        {
            if (dir.LinkTarget is not null)
            {
                var resolvedDir = Path.GetFullPath(dir.LinkTarget);
                return Path.GetFullPath(Path.Combine(resolvedDir, Path.GetRelativePath(dir.FullName, path)));
            }
            dir = dir.Parent;
        }

        return path;
    }

    private bool IsPathAllowed(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        foreach (var basePath in _allowedBasePaths)
        {
            // Match on directory boundary, not just string prefix
            var baseWithSep = basePath.EndsWith(Path.DirectorySeparatorChar)
                ? basePath
                : basePath + Path.DirectorySeparatorChar;

            if (normalized.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)
                || normalized.Equals(basePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ValidateWriteTarget(string fullPath)
    {
        foreach (var sysDir in SystemDirectoryBlocklist)
        {
            if (fullPath.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Blocked write to system directory: {Path}", fullPath);
                throw new UnauthorizedAccessException("Cannot write to system directories.");
            }
        }
    }

    private static void ValidatePattern(string? pattern)
    {
        if (pattern is not null && (pattern.Contains('/') || pattern.Contains('\\')))
            throw new ArgumentException("Search pattern must not contain path separators.");
    }

    private static HashSet<string> BuildSystemBlocklist()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.System));

        return dirs;

        static void AddIfNotEmpty(HashSet<string> set, string path)
        {
            if (!string.IsNullOrEmpty(path))
                set.Add(Path.GetFullPath(path));
        }
    }
}
