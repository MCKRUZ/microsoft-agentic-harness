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
/// <para>
/// This is the surface the <em>model</em> reaches, through <see cref="FileSystemTool"/>, and it can
/// write. It is confined to <c>AppConfig:Infrastructure:FileSystem:AllowedBasePaths</c> and nothing
/// else — in particular <b>not</b> to the skill content roots. Skill loading has its own read-only
/// sandbox (<c>SkillFileReader</c>) precisely so that widening it cannot widen this; see
/// <see cref="SandboxedPathGuard"/> for the reasoning.
/// </para>
/// <para>
/// The allow/deny decision itself lives in <see cref="SandboxedPathGuard"/>, shared with that reader
/// so the two sandboxes cannot drift on what "inside" means.
/// </para>
/// </remarks>
public sealed class FileSystemService : IFileSystemService
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxSearchResults = 100;
    private const int MaxFilesScanned = 1_000;
    private const int SnippetMaxLength = 200;

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
    private readonly SandboxedPathGuard _guard;

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
        _guard = new SandboxedPathGuard(logger, allowedBasePaths, protectedPaths);

        if (_guard.AllowedPathCount == 0)
            _logger.LogWarning("FileSystemService initialized with zero allowed base paths — all operations will be denied");
        else
            _logger.LogInformation("FileSystemService initialized with {PathCount} allowed base paths", _guard.AllowedPathCount);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = _guard.ResolveAndValidate(path);

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

        var fullPath = _guard.ResolveAndValidate(path, write: true);

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
        var fullPath = _guard.ResolveAndValidate(path);

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
        var fullPath = _guard.ResolveAndValidate(path);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<FileSearchResult>();
        var searchPattern = string.IsNullOrEmpty(pattern) ? "*.*" : pattern;
        var filesScanned = 0;

        // Lives only for this call — see SandboxedPathGuard.ResolveVerdict for why it must not
        // become process-wide.
        var directoryVerdicts = new Dictionary<string, SandboxedPathGuard.DirectoryVerdict>(PathScope.Comparer);

        foreach (var file in EnumerateFilesSkippingIgnored(
            fullPath, searchPattern, directoryVerdicts, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++filesScanned > MaxFilesScanned)
            {
                _logger.LogWarning("Search scan limit reached ({Limit} files)", MaxFilesScanned);
                break;
            }

            if (results.Count >= MaxSearchResults)
                break;

            await SearchFileAsync(file, fullPath, searchTerm, results, directoryVerdicts, cancellationToken);
        }

        _logger.LogDebug("Search complete: {ResultCount} results from {ScannedCount} files scanned", results.Count, filesScanned);
        return results;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = _guard.ResolveAndValidate(path);
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

    /// <summary>
    /// Breadth-first walk of <paramref name="root"/>, yielding files while pruning any subtree the
    /// sandbox refuses, and recording each directory's verdict in <paramref name="directoryVerdicts"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arms of the sandbox decision prune, not just the deny arm. A junction or symlink
    /// planted in the workspace is not <em>protected</em> — its target is somewhere else entirely —
    /// so a deny-only check descends into it happily, and every file it then yields carries a
    /// literal, workspace-shaped path that satisfies a workspace-rooted allowlist. Pruning on the
    /// canonical target is what keeps the walk inside the sandbox, and it has to happen here:
    /// canonicalization resolves an entry's own link, not links in its parent components, so a
    /// directory reached <em>below</em> an unpruned link would canonicalize to its literal path and
    /// look legitimate.
    /// </para>
    /// <para>
    /// Directory verdicts are resolved without the memo. Parent-verdict inheritance is sound for
    /// files only — a file under an unprotected directory cannot itself be a protected root, but a
    /// subdirectory can be exactly that, <c>.agent-state</c> being an ordinary non-reparse-point
    /// directory inside an unprotected workspace.
    /// </para>
    /// </remarks>
    /// <param name="root">The already-validated absolute search root.</param>
    /// <param name="pattern">The file-name pattern to match.</param>
    /// <param name="directoryVerdicts">
    /// Per-operation verdict memo; see <see cref="SandboxedPathGuard.ResolveVerdict"/>.
    /// </param>
    /// <param name="cancellationToken">Token to observe while walking.</param>
    private IEnumerable<string> EnumerateFilesSkippingIgnored(
        string root, string pattern, Dictionary<string, SandboxedPathGuard.DirectoryVerdict> directoryVerdicts,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        // Normalized before the verdict call, not only to key the memo: Canonicalize documents a
        // normalized input as a precondition and returns its argument unchanged on the error path.
        var normalizedRoot = PathScope.Normalize(root);
        directoryVerdicts[normalizedRoot] = _guard.ResolveVerdict(normalizedRoot);

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
                // A performance filter (build artifacts, VCS internals), never a security boundary
                // — the sandbox decision below is what prunes protected and escaping subtrees.
                if (SearchSkipDirectories.Contains(Path.GetFileName(sub)))
                    continue;

                // No memo here, and both arms prune — see this method's remarks for why.
                var normalizedSub = PathScope.Normalize(sub);
                var verdict = _guard.ResolveVerdict(normalizedSub);
                directoryVerdicts[normalizedSub] = verdict;

                if (!verdict.IsProtected && _guard.IsUnderAllowedBase(verdict.CanonicalPath))
                    queue.Enqueue(sub);
            }
        }
    }

    private async Task SearchFileAsync(
        string filePath, string basePath, string searchTerm,
        List<FileSearchResult> results, Dictionary<string, SandboxedPathGuard.DirectoryVerdict> directoryVerdicts,
        CancellationToken cancellationToken)
    {
        // Last line of defence before any file content is read. The directory filter above
        // already prunes protected and out-of-sandbox subtrees; this catches a file reached any
        // other way (a file directly under an allowed root, a file that is itself a link, a future
        // enumeration change) so no content leaves this method without having passed the same gate
        // a direct read does. "The same gate" is load-bearing and was previously untrue: the
        // direct-read gate resolves links before comparing, and this one compared the literal
        // enumerated path, so a link inside the workspace read through it whatever it pointed at.
        if (!_guard.IsPathAllowed(filePath, directoryVerdicts))
        {
            _logger.LogWarning("Skipped search of disallowed path: {Path}", filePath);
            return;
        }

        // The same identity gate the direct read applies, for the same reason: search returns file
        // CONTENT, so leaving it out would close reads of a hard-linked database while leaving the
        // disclosure path — grep the workspace for a needle, read it out of the alias — wide open.
        // On cost: this is one extra handle open per file actually scanned (bounded by
        // MaxFilesScanned), paid only on hosts with protected paths, against a method that already
        // opens and reads every one of those files line by line. It is not the per-file link
        // RESOLUTION the directory-verdict memo exists to avoid — that walks parent components and
        // is paid per enumerated entry; this is a single stat-and-open on files already being read.
        if (!_guard.IsSingleLinked(filePath))
            return;

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

    private static void ValidatePattern(string? pattern)
    {
        if (pattern is not null && (pattern.Contains('/') || pattern.Contains('\\')))
            throw new ArgumentException("Search pattern must not contain path separators.");
    }
}
