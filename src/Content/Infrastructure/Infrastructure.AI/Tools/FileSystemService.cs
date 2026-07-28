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

    // Harness-owned state directories the tool may never read or write, regardless of the
    // configured allowlist. Holds the governance-state database — pending escalations, their
    // resolved verdicts, and change proposals. An agent that could read that file could mine
    // approval payloads; one that could edit it could forge a human approval, which the
    // reconciler would then re-drive into the hash-chained compliance audit log.
    //
    // Compared as CANONICAL ABSOLUTE PATHS, never as directory names: name matching is
    // defeated by a Windows 8.3 short alias (AGENT-~1) or any other alias that resolves to the
    // same directory but spells it differently. The canonical form comes from the same
    // resolver the database registration uses, so the two cannot drift.
    private readonly HashSet<string> _protectedPaths;

    // Directories skipped during recursive search — build artifacts and VCS internals
    // would exhaust the scan limit before reaching actual source files.
    private static readonly HashSet<string> SearchSkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "bin", "obj",
        "node_modules", ".npm",
        ".vs", ".vscode", ".idea",
        ".claude", ".worktrees",
        "packages", "publish",
        "logs",
    };

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
    /// <param name="protectedPaths">
    /// Absolute directory paths that are denied even when they fall inside
    /// <paramref name="allowedBasePaths"/> — the harness's own governance-state directory.
    /// The deny check wins over the allowlist and applies to reads, writes, and the recursive
    /// search walk alike. Optional; omit for callers with no protected state.
    /// </param>
    public FileSystemService(
        ILogger<FileSystemService> logger,
        IEnumerable<string> allowedBasePaths,
        IEnumerable<string>? protectedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(allowedBasePaths);

        _logger = logger;
        _allowedBasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in protectedPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
                _protectedPaths.Add(CanonicalizePath(path));
        }

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

        foreach (var file in EnumerateFilesSkippingIgnored(fullPath, searchPattern, cancellationToken))
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

    private IEnumerable<string> EnumerateFilesSkippingIgnored(
        string root, string pattern, CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirs)
            {
                // The protected-path check must be repeated here, not just at the search root:
                // the walk descends from an allowed root into whatever it finds, and the skip
                // list is a performance filter (build artifacts, VCS internals), not a security
                // boundary. Without this a search rooted at the workspace would descend into the
                // governance-state directory and read the database's pages as text.
                if (!SearchSkipDirectories.Contains(Path.GetFileName(sub)) && !IsProtectedPath(sub))
                    queue.Enqueue(sub);
            }
        }
    }

    private async Task SearchFileAsync(
        string filePath, string basePath, string searchTerm,
        List<FileSearchResult> results, CancellationToken cancellationToken)
    {
        // Last line of defence before any file content is read. The directory filter above
        // already prunes protected subtrees; this catches a protected file reached any other
        // way (a file directly under an allowed root, a future enumeration change) so no
        // content leaves this method without having passed the same gate a direct read does.
        if (!IsPathAllowed(filePath))
        {
            _logger.LogWarning("Skipped search of disallowed path: {Path}", filePath);
            return;
        }

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

        // Deny list wins over the allowlist. Protected directories hold the harness's own
        // governance state — the SQLite database of approval verdicts — and typically sit
        // under a configured base path, so allowlisting alone would leave them reachable.
        // An agent able to edit that file could forge an approval verdict, so the tool must
        // not be able to read or write it under any configuration.
        if (IsProtectedPath(normalized))
        {
            _logger.LogWarning("Blocked access to protected harness state directory: {Path}", normalized);
            return false;
        }

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

    /// <summary>
    /// True when <paramref name="fullPath"/> is, or lives under, a protected harness-state
    /// directory.
    /// </summary>
    /// <remarks>
    /// Compares canonicalized absolute paths on a directory boundary, so a legitimate sibling
    /// such as <c>.agent-state-docs</c> is unaffected while an alias that merely spells the
    /// protected directory differently (a Windows 8.3 short name, a symlinked parent already
    /// resolved upstream) still matches.
    /// </remarks>
    /// <param name="fullPath">An absolute path.</param>
    private bool IsProtectedPath(string fullPath)
    {
        if (_protectedPaths.Count == 0)
            return false;

        var normalized = CanonicalizePath(fullPath);
        foreach (var protectedPath in _protectedPaths)
        {
            var withSeparator = protectedPath.EndsWith(Path.DirectorySeparatorChar)
                ? protectedPath
                : protectedPath + Path.DirectorySeparatorChar;

            if (normalized.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a path to its canonical absolute form, so an alias cannot sidestep a comparison
    /// against the long name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two steps cover different aliases. <see cref="Path.GetFullPath(string)"/> is what
    /// handles Windows 8.3 short names: on Windows its normalization expands any component that
    /// exists on disk, turning <c>PROGRA~1</c> into <c>Program Files</c>. The
    /// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> step then covers symlinks and
    /// junctions, which <c>GetFullPath</c> does leave intact.
    /// </para>
    /// <para>
    /// A component that does not exist is left exactly as written, because there is nothing on
    /// disk to expand it against. That is harmless for the protected-path check: a protected
    /// directory that does not exist holds nothing to protect, and a literal <c>AGENT-~1</c>
    /// path then names a genuinely different directory rather than aliasing the protected one.
    /// </para>
    /// </remarks>
    /// <param name="path">The path to canonicalize.</param>
    private static string CanonicalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            // ResolveLinkTarget(returnFinalTarget) canonicalizes an existing entry; for a path
            // that does not exist yet the normalized form is the best available answer.
            var info = Directory.Exists(full) ? new DirectoryInfo(full) : (FileSystemInfo)new FileInfo(full);
            return info.Exists ? Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? info.FullName) : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
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
