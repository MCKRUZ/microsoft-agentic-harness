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
    // resolved verdicts, and change proposals. An agent that could read that file could mine the
    // approval payloads it carries; one that could write it could truncate or corrupt the
    // harness's own governance state. It could NOT forge an approval verdict: every outcome is
    // HMAC-sealed by AttestationGovernanceRecordSealer with a key the agent has no access to, the
    // seal is bound to the row id, and both read paths in EfCoreEscalationStateStore verify it and
    // quarantine on failure. So the exposure this list closes is disclosure and tamper/denial of
    // service, not forgery.
    //
    // Compared as CANONICAL ABSOLUTE PATHS, never as directory names: name matching is
    // defeated by a Windows 8.3 short alias (AGENT-~1) or any other alias that resolves to the
    // same directory but spells it differently. The canonical form comes from the same
    // resolver the database registration uses, so the two cannot drift.
    //
    // This deny list cannot see through a hard link, and nothing path-based can: a hard link is a
    // second directory entry for the same file, not a reparse point, so there is no link target to
    // resolve and its canonical form is itself. That gap is closed by asking about the file's
    // identity instead of its path — see IsSingleLinked — because a hard link needs only the same
    // VOLUME as its target, which no allowlist geometry can prevent.
    //
    // Legitimately EMPTY on a host with no governance state to guard. The composition root
    // (DependencyInjection.ResolveGovernanceStateProtectedPaths) supplies the governance-state
    // directory only when a durable-state toggle is on or a database from an earlier run is still
    // on disk; in the shipped default neither holds. An empty set disarms both this deny list and
    // the identity check below, which is the correct outcome when there is nothing to alias.
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

        // PathScope.Comparer, never a hardcoded StringComparer.OrdinalIgnoreCase: on Linux
        // "/srv/state" and "/srv/State" are two different directories, and case-insensitive
        // hashing would collapse them into one entry. For the deny set that collapse is
        // fail-OPEN — the second protected directory silently stops being protected.
        _allowedBasePaths = new HashSet<string>(PathScope.Comparer);
        _protectedPaths = new HashSet<string>(PathScope.Comparer);

        // Both sets are stored in canonical, PathScope-normalized form (absolute, links resolved,
        // trailing separator trimmed). Normalized because every comparison against them goes
        // through PathScope.IsSameOrUnderNormalized, which requires normalized inputs on both
        // sides; canonical because the paths a comparison is fed at runtime are canonicalized, and
        // comparing a canonical target against a literal base misses whenever the configured base
        // is itself reached through a link.
        foreach (var path in protectedPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
                _protectedPaths.Add(SandboxPathCanonicalizer.Canonicalize(PathScope.Normalize(path)));
        }

        foreach (var path in allowedBasePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _allowedBasePaths.Add(SandboxPathCanonicalizer.Canonicalize(PathScope.Normalize(path)));
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

        // Lives only for this call — see IsProtectedPath for why it must not become process-wide.
        var directoryVerdicts = new Dictionary<string, DirectoryVerdict>(PathScope.Comparer);

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
    /// <param name="directoryVerdicts">Per-operation verdict memo; see <see cref="ResolveVerdict"/>.</param>
    /// <param name="cancellationToken">Token to observe while walking.</param>
    private IEnumerable<string> EnumerateFilesSkippingIgnored(
        string root, string pattern, Dictionary<string, DirectoryVerdict> directoryVerdicts,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        // Normalized before the verdict call, not only to key the memo: Canonicalize documents a
        // normalized input as a precondition and returns its argument unchanged on the error path.
        var normalizedRoot = PathScope.Normalize(root);
        directoryVerdicts[normalizedRoot] = ResolveVerdict(normalizedRoot);

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
                var verdict = ResolveVerdict(normalizedSub);
                directoryVerdicts[normalizedSub] = verdict;

                if (!verdict.IsProtected && IsUnderAllowedBase(verdict.CanonicalPath))
                    queue.Enqueue(sub);
            }
        }
    }

    private async Task SearchFileAsync(
        string filePath, string basePath, string searchTerm,
        List<FileSearchResult> results, Dictionary<string, DirectoryVerdict> directoryVerdicts,
        CancellationToken cancellationToken)
    {
        // Last line of defence before any file content is read. The directory filter above
        // already prunes protected and out-of-sandbox subtrees; this catches a file reached any
        // other way (a file directly under an allowed root, a file that is itself a link, a future
        // enumeration change) so no content leaves this method without having passed the same gate
        // a direct read does. "The same gate" is load-bearing and was previously untrue: the
        // direct-read gate resolves links before comparing, and this one compared the literal
        // enumerated path, so a link inside the workspace read through it whatever it pointed at.
        if (!IsPathAllowed(filePath, directoryVerdicts))
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
        if (!IsSingleLinked(filePath))
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

        // Identity, after path. Everything above reasons about what the path spells; this asks the
        // operating system what file it actually names. Only the second question sees a hard link.
        if (!IsSingleLinked(fullPath))
            throw new UnauthorizedAccessException(HardLinkDenialMessage);

        if (write)
            ValidateWriteTarget(fullPath);

        return fullPath;
    }

    /// <summary>
    /// The refusal handed to a caller whose path names a file the sandbox cannot prove is unique.
    /// </summary>
    private const string HardLinkDenialMessage =
        "Path names a file the sandbox cannot prove has a single directory entry, so it cannot " +
        "rule out that the file is a hard link aliasing protected harness state.";

    /// <summary>
    /// True when <paramref name="fullPath"/> names a file with exactly one directory entry — the
    /// only case in which a path-based sandbox decision about it is trustworthy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a path check is not enough.</b> A hard link is a second directory entry for one file,
    /// carrying no reparse point, so it resolves to itself and reads to every check above as an
    /// ordinary workspace file. The startup validator keeps protected directories out of the
    /// allowed tree, which is worth doing, but it is not what closes this: a hard link requires the
    /// same VOLUME as its target, not the same subtree, and the shipped default puts
    /// <c>workspace</c> and <c>.agent-state</c> side by side on one volume. Non-containment is
    /// strictly narrower than volume separation, and volume separation is unreachable anyway
    /// because <c>GovernanceStatePaths.Resolve</c> pins the database under the application base
    /// directory.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> <see cref="HardLinkInspector.LinkCount.Unknown"/> — an unimplemented
    /// platform (anything but Windows and Linux), a failed platform call, or an untrustworthy
    /// count — denies. A consumer running on such a platform with protected paths configured gets
    /// a closed file tool rather than a silently unguarded one.
    /// </para>
    /// <para>
    /// <b>Scoped to hosts that have something to protect.</b> With no protected paths configured
    /// there is nothing a hard link could alias, so the inspection is skipped outright and callers
    /// with no harness state pay nothing — neither the handle open nor the platform restriction.
    /// This is the shipped default rather than an edge case: the composition root withholds the
    /// governance-state directory until a durable-state toggle is on or a database from an earlier
    /// run exists, so an out-of-the-box host runs this tool on every platform, macOS included.
    /// </para>
    /// </remarks>
    /// <param name="fullPath">An absolute path. Need not exist; a file yet to be created passes.</param>
    private bool IsSingleLinked(string fullPath)
    {
        if (_protectedPaths.Count == 0)
            return true;

        var linkCount = HardLinkInspector.Inspect(fullPath);
        if (linkCount == HardLinkInspector.LinkCount.Single)
            return true;

        _logger.LogWarning(
            "Blocked access to a path the sandbox cannot prove is singly-linked: {Path} ({LinkCount})",
            fullPath,
            linkCount);
        return false;
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

    /// <summary>
    /// The sandbox decision for one path: whether it is denied outright, and the canonical
    /// location the allowlist comparison must be made against.
    /// </summary>
    /// <remarks>
    /// The two travel together because they are answers to the same question — "what does this
    /// path actually name?" — and separating them is what let a symlink escape the sandbox: the
    /// deny arm consumed the canonical form while the allow arm compared the literal one.
    /// </remarks>
    /// <param name="IsProtected">
    /// <see langword="true"/> when the path is, or lives under, a protected harness-state directory.
    /// </param>
    /// <param name="CanonicalPath">
    /// The path's canonical absolute form, with links resolved. Valid on both arms.
    /// </param>
    private readonly record struct DirectoryVerdict(bool IsProtected, string CanonicalPath);

    /// <param name="fullPath">An absolute path.</param>
    /// <param name="directoryVerdicts">
    /// Optional per-operation sandbox-verdict memo; see <see cref="ResolveVerdict"/>.
    /// </param>
    private bool IsPathAllowed(string fullPath, Dictionary<string, DirectoryVerdict>? directoryVerdicts = null)
    {
        var verdict = ResolveVerdict(PathScope.Normalize(fullPath), directoryVerdicts);

        // Deny list wins over the allowlist. Protected directories hold the harness's own
        // governance state — the SQLite database of approval verdicts — and typically sit
        // under a configured base path, so allowlisting alone would leave them reachable.
        // An agent able to read that file could mine approval payloads, and one able to write it
        // could truncate or corrupt the harness's own state; the HMAC seal on every row is what
        // stops it forging a verdict. The tool must not reach it under any configuration.
        if (verdict.IsProtected)
        {
            _logger.LogWarning(
                "Blocked access to protected harness state directory: {Path}", verdict.CanonicalPath);
            return false;
        }

        // Compared against the CANONICAL path, never the literal one. A junction at
        // workspace\notes pointing at C:\Users\victim\.ssh yields files whose literal paths all
        // look like workspace\notes\..., which satisfy any workspace-rooted allowlist; only the
        // resolved target reveals that they are outside the sandbox.
        return IsUnderAllowedBase(verdict.CanonicalPath);
    }

    /// <summary>
    /// True when <paramref name="canonicalPath"/> sits inside a configured allowed base path.
    /// </summary>
    /// <remarks>
    /// PathScope matches on a directory boundary, so a sibling whose name merely starts with an
    /// allowed root (<c>C:\workspace-backup</c> against <c>C:\workspace</c>) is not mistaken for a
    /// child. Both operands are canonical: the argument by contract, the base paths because the
    /// constructor canonicalizes them once.
    /// </remarks>
    /// <param name="canonicalPath">A canonical absolute path.</param>
    private bool IsUnderAllowedBase(string canonicalPath) =>
        _allowedBasePaths.Any(basePath => PathScope.IsSameOrUnderNormalized(canonicalPath, basePath));

    /// <summary>
    /// Canonicalizes <paramref name="normalizedPath"/> and decides whether it is protected,
    /// returning both halves of the sandbox decision.
    /// </summary>
    /// <remarks>
    /// Compares canonicalized absolute paths on a directory boundary, so a legitimate sibling
    /// such as <c>.agent-state-docs</c> is unaffected while an alias that merely spells the
    /// protected directory differently (a Windows 8.3 short name, a symlink, a junctioned parent)
    /// still matches.
    /// </remarks>
    /// <param name="normalizedPath">
    /// An absolute path, already <see cref="PathScope.Normalize"/>d — the precondition
    /// <see cref="SandboxPathCanonicalizer.Canonicalize"/> documents.
    /// </param>
    /// <param name="directoryVerdicts">
    /// <para>
    /// Optional memo of directory path to verdict, scoped to a <em>single</em> search operation and
    /// threaded through the walk by <see cref="EnumerateFilesSkippingIgnored"/>. Canonicalizing a path
    /// costs a stat plus a link-resolution handle open; without the memo a recursive search pays that
    /// for every file it scans rather than for every directory it descends into.
    /// </para>
    /// <para>
    /// The scope is deliberately per-operation and must NOT be promoted to a process-lifetime cache.
    /// An agent can create a benign file, let its verdict be recorded, then replace it with a symlink
    /// into the protected directory; a cache that outlives the operation would serve the stale
    /// "allowed" verdict and hand over the approval-verdict database. Per-operation scope bounds that
    /// staleness window to the same window the directory prune already has.
    /// </para>
    /// </param>
    private DirectoryVerdict ResolveVerdict(
        string normalizedPath, Dictionary<string, DirectoryVerdict>? directoryVerdicts = null)
    {
        if (directoryVerdicts is not null
            && TryInheritParentVerdict(normalizedPath, directoryVerdicts, out var inherited))
        {
            return inherited;
        }

        var canonical = SandboxPathCanonicalizer.Canonicalize(normalizedPath);
        var isProtected = _protectedPaths.Any(protectedPath =>
            PathScope.IsSameOrUnderNormalized(canonical, protectedPath));

        return new DirectoryVerdict(isProtected, canonical);
    }

    /// <summary>
    /// Resolves <paramref name="normalizedPath"/>'s verdict from its already-canonicalized parent
    /// directory when that is sound, avoiding a link resolution per file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inheritance is only sound for a plain file: a subdirectory can itself be a protected root,
    /// and a reparse point resolves somewhere its parent says nothing about. Both are rejected
    /// here, by the method rather than by convention, and fall through to full canonicalization.
    /// Reading the attributes costs a stat but no handle open, which is the syscall being avoided.
    /// </para>
    /// <para>
    /// For an entry that passes that test the composed path is exact, not an approximation: a
    /// non-reparse-point entry genuinely lives inside its parent directory, so appending its leaf
    /// name to the parent's canonical directory names the same location a full canonicalization
    /// would return. That exactness is what makes the memo safe to feed the allowlist comparison —
    /// inheriting only the boolean, as this method used to, left the allow arm with a literal path
    /// whose parent had been canonicalized but which had not.
    /// </para>
    /// <para>
    /// A parent verdict of <see langword="true"/> transfers unconditionally — everything under a
    /// protected directory is protected, including a link planted there — but is canonicalized in
    /// full rather than composed, because a reparse point in that position does not occupy the
    /// composed location. That branch is effectively unreachable during a search (protected
    /// directories are never enqueued), so the extra resolve costs nothing in practice.
    /// </para>
    /// </remarks>
    private static bool TryInheritParentVerdict(
        string normalizedPath,
        Dictionary<string, DirectoryVerdict> directoryVerdicts,
        out DirectoryVerdict verdict)
    {
        verdict = default;

        var parent = Path.GetDirectoryName(normalizedPath);
        if (parent is null
            || !directoryVerdicts.TryGetValue(PathScope.Normalize(parent), out var parentVerdict))
        {
            return false;
        }

        if (parentVerdict.IsProtected)
        {
            verdict = new DirectoryVerdict(true, SandboxPathCanonicalizer.Canonicalize(normalizedPath));
            return true;
        }

        try
        {
            // Directory: a subdirectory can itself be a protected root. ReparsePoint: a link
            // resolves somewhere its parent says nothing about. Enforced here rather than left as a
            // caller contract, because a caller that gets it wrong fails open and fails silently.
            var attributes = File.GetAttributes(normalizedPath);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            // Unreadable, already gone, or a path shape the runtime rejects outright: fall through
            // to the full check rather than guess. NotSupportedException is in the filter so it
            // produces a clean deny instead of escaping SearchFilesAsync unhandled.
            return false;
        }

        verdict = new DirectoryVerdict(
            false,
            PathScope.Normalize(Path.Combine(
                parentVerdict.CanonicalPath, Path.GetFileName(normalizedPath))));
        return true;
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
