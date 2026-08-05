using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Decides whether a path may be touched: the allow/deny geometry, link resolution, and file-identity
/// checks that confine a file-access surface to a configured set of directories.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="FileSystemService"/> so the harness can run <em>more than one</em>
/// sandbox without owning more than one implementation of the rules. There are two today and they
/// deliberately permit different directories:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="FileSystemService"/> — reachable by the model through <see cref="FileSystemTool"/>,
///     and able to <b>write</b>. Confined to <c>AppConfig:Infrastructure:FileSystem:AllowedBasePaths</c>.
///   </description></item>
///   <item><description>
///     <c>SkillFileReader</c> — the harness's own skill loader. Confined to the configured skill
///     content roots and exposes <b>no write operation at all</b>.
///   </description></item>
/// </list>
/// <para>
/// <b>Why they are separate instances rather than one widened allowlist.</b> Skill content lives
/// outside the model's sandbox by default (<c>skills</c> versus <c>workspace</c>), so routing skill
/// loading through the model's own service would have required adding the skill roots to it. That
/// service can write, and <see cref="FileSystemTool"/> exposes its write operation to the model with
/// no approval gate — so the widening would have handed the model the ability to rewrite its own
/// <c>SKILL.md</c> files, including the <c>allowed-tools</c> list that constrains it. Two narrow
/// sandboxes over one shared rulebook is what closes the skill-loading bypass without opening that
/// one (issue #247).
/// </para>
/// </remarks>
internal sealed class SandboxedPathGuard
{
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

    private readonly ILogger _logger;
    private readonly HashSet<string> _allowedBasePaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="SandboxedPathGuard"/> class.
    /// </summary>
    /// <param name="logger">Receives a warning for every refusal, and the path that caused it.</param>
    /// <param name="allowedBasePaths">
    /// The set of absolute directory paths this guard permits. Paths are canonicalized and normalized
    /// once, here. An empty set denies everything — see <see cref="HasAllowedPaths"/>.
    /// </param>
    /// <param name="protectedPaths">
    /// Absolute directory paths denied even when they fall inside <paramref name="allowedBasePaths"/>.
    /// The deny check wins over the allowlist. Optional; omit for callers with no protected state.
    /// </param>
    public SandboxedPathGuard(
        ILogger logger,
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
    }

    /// <summary>
    /// The number of directories this guard permits. Zero means every operation is refused.
    /// </summary>
    public int AllowedPathCount => _allowedBasePaths.Count;

    /// <summary>
    /// <see langword="false"/> when the guard was given nothing to allow, in which case it denies
    /// everything. Callers surface this at construction so a misconfiguration is visible in the log
    /// rather than only as a refusal on first use.
    /// </summary>
    public bool HasAllowedPaths => _allowedBasePaths.Count > 0;

    /// <summary>
    /// The refusal handed to a caller whose path names a file the sandbox cannot prove is unique.
    /// </summary>
    public const string HardLinkDenialMessage =
        "Path names a file the sandbox cannot prove has a single directory entry, so it cannot " +
        "rule out that the file is a hard link aliasing protected harness state.";

    /// <summary>
    /// Resolves a caller-supplied path to an absolute path and validates it against the
    /// sandbox (input validation, allowlist, symlink resolution, file identity, write blocklist).
    /// </summary>
    /// <param name="path">The caller-supplied path, absolute or relative to an allowed base.</param>
    /// <param name="write">When <see langword="true"/>, additionally refuses system directories.</param>
    /// <returns>The validated absolute path.</returns>
    /// <exception cref="ArgumentException">The path is empty or contains traversal patterns.</exception>
    /// <exception cref="UnauthorizedAccessException">The path is refused by the sandbox.</exception>
    public string ResolveAndValidate(string path, bool write = false)
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
    public bool IsSingleLinked(string fullPath)
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
    public readonly record struct DirectoryVerdict(bool IsProtected, string CanonicalPath);

    /// <param name="fullPath">An absolute path.</param>
    /// <param name="directoryVerdicts">
    /// Optional per-operation sandbox-verdict memo; see <see cref="ResolveVerdict"/>.
    /// </param>
    public bool IsPathAllowed(string fullPath, Dictionary<string, DirectoryVerdict>? directoryVerdicts = null)
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
    public bool IsUnderAllowedBase(string canonicalPath) =>
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
    /// threaded through the walk by <c>FileSystemService.EnumerateFilesSkippingIgnored</c>.
    /// Canonicalizing a path costs a stat plus a link-resolution handle open; without the memo a
    /// recursive search pays that for every file it scans rather than for every directory it
    /// descends into.
    /// </para>
    /// <para>
    /// The scope is deliberately per-operation and must NOT be promoted to a process-lifetime cache.
    /// An agent can create a benign file, let its verdict be recorded, then replace it with a symlink
    /// into the protected directory; a cache that outlives the operation would serve the stale
    /// "allowed" verdict and hand over the approval-verdict database. Per-operation scope bounds that
    /// staleness window to the same window the directory prune already has.
    /// </para>
    /// </param>
    public DirectoryVerdict ResolveVerdict(
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
