using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.RunEvalSuite;

/// <summary>
/// Decides whether a requested evaluation dataset path may be read, and returns the canonical path to
/// read it from.
/// </summary>
/// <remarks>
/// Separate from the handler so the confinement rules can be tested directly against traversal,
/// symlink escape, and root-prefix edge cases — a check that only exists inside a handler tends to be
/// exercised only through the happy path.
/// </remarks>
public interface IEvalDatasetPathGuard
{
    /// <summary>
    /// Resolves <paramref name="requestedPath"/> to a canonical absolute path, or explains why it may
    /// not be read.
    /// </summary>
    /// <param name="requestedPath">The path as supplied by the caller.</param>
    /// <returns>
    /// The canonical path on success. On failure the message is deliberately coarse when confinement
    /// is active — see the implementation for why.
    /// </returns>
    EvalDatasetPathDecision Resolve(string requestedPath);
}

/// <summary>The outcome of guarding one dataset path.</summary>
/// <param name="IsAllowed">Whether the path may be read.</param>
/// <param name="CanonicalPath">The canonical absolute path when allowed; otherwise <see langword="null"/>.</param>
/// <param name="Reason">A caller-safe explanation when refused; otherwise <see langword="null"/>.</param>
public readonly record struct EvalDatasetPathDecision(bool IsAllowed, string? CanonicalPath, string? Reason)
{
    /// <summary>Creates an allowing decision.</summary>
    public static EvalDatasetPathDecision Allow(string canonicalPath) => new(true, canonicalPath, null);

    /// <summary>Creates a refusing decision.</summary>
    public static EvalDatasetPathDecision Refuse(string reason) => new(false, null, reason);
}

/// <summary>
/// Default <see cref="IEvalDatasetPathGuard"/>: confines dataset reads to
/// <c>AppConfig:AI:Evaluation:DatasetRoots</c> when any root is configured, and leaves them unconfined
/// when none is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Unconfined-by-default is a deliberate compromise, not an oversight.</strong> Evaluation's
/// only dispatcher today is the EvalRunner CLI, where the caller is a developer on their own machine
/// and pointing the runner at a file anywhere on disk is the normal workflow. Making roots mandatory
/// would break that for no gain, because a local developer can read those files anyway. The protection
/// is needed exactly when the caller is <em>not</em> the machine's owner, and a host in that position
/// configures roots — the HTTP surface refuses to run without them rather than silently inheriting the
/// permissive default.
/// </para>
/// <para>
/// <strong>Refusals are deliberately vague when confined.</strong> "Outside the allowed roots" and
/// "does not exist" are reported identically, because distinguishing them turns the endpoint into a
/// filesystem oracle: a caller could map the disk by watching which of two error messages came back.
/// When unconfined there is no untrusted caller to protect against and the specific path is echoed,
/// which is what makes the CLI usable.
/// </para>
/// <para>
/// <strong>Confinement ratchets one way.</strong> Adding a root at runtime takes effect immediately;
/// emptying the list does not loosen anything, because a host that started confined refuses every path
/// rather than reverting to the permissive branch. The startup check that stops an unconfined host
/// booting runs once, and a later configuration reload would never re-run it.
/// </para>
/// <para>
/// <strong>What this cannot see.</strong> Confinement is path-based, so it follows symbolic links and
/// junctions but is blind to <em>hard</em> links: a hard link inside a root has no target to resolve and
/// is indistinguishable from an ordinary file there. That is inherent to the approach, not a gap in the
/// implementation — it means a configured root must not be writable by anyone the host would not let
/// read arbitrary files.
/// </para>
/// </remarks>
public sealed class EvalDatasetPathGuard : IEvalDatasetPathGuard
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly EvalConfinementLatch _latch;

    /// <summary>Initializes the guard with live configuration and the host's startup verdict.</summary>
    /// <param name="config">
    /// Read per call rather than captured, so <em>adding</em> a root takes effect without a restart.
    /// </param>
    /// <param name="latch">
    /// Whether a composition root verified confinement at startup. Deliberately not derived from
    /// <paramref name="config"/> here: this guard is a lazy singleton, so its constructor runs on the
    /// first dispatch rather than at boot, and a latch computed here would record whatever the
    /// configuration happened to say by then.
    /// </param>
    public EvalDatasetPathGuard(IOptionsMonitor<AppConfig> config, EvalConfinementLatch latch)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(latch);
        _config = config;
        _latch = latch;
    }

    /// <inheritdoc />
    public EvalDatasetPathDecision Resolve(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return EvalDatasetPathDecision.Refuse("Dataset paths must not be empty strings.");

        var roots = UsableRoots(_config.CurrentValue);

        // Confinement ratchets one way. Adding a root at runtime tightens and is honoured immediately;
        // emptying the list cannot loosen, because a host that verified confinement at startup keeps
        // refusing regardless. Without the latch, a configuration reload that dropped DatasetRoots would
        // silently downgrade a confined host to arbitrary-file-read.
        var confined = _latch.StartedConfined || roots.Count > 0;

        if (confined && roots.Count == 0)
            return EvalDatasetPathDecision.Refuse("Dataset is not available.");

        string canonical;
        try
        {
            // Canonicalise before any comparison. A path containing ".." compares against a root as a
            // string quite happily while resolving somewhere else entirely.
            canonical = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return EvalDatasetPathDecision.Refuse(
                confined ? "Dataset is not available." : $"Dataset path is not a valid path: {requestedPath}");
        }

        if (!confined)
        {
            return File.Exists(canonical)
                ? EvalDatasetPathDecision.Allow(canonical)
                : EvalDatasetPathDecision.Refuse($"Dataset file not found: {requestedPath}");
        }

        // Resolve symlinks only after canonicalising. A link inside a root may point outside it, and
        // the file the loader ultimately opens is the target, not the link.
        var walk = new LinkWalk();
        var effective = ResolveSegments(canonical, walk);

        // A walk that ran out of budget did not finish, so `effective` may still be a link — and a link
        // that happens to sit inside a root would otherwise be admitted while opening a target outside
        // it. Not knowing where a path leads is a refusal, not a pass.
        if (walk.Exhausted)
            return EvalDatasetPathDecision.Refuse("Dataset is not available.");

        foreach (var canonicalRoot in ResolvedRoots(roots))
        {
            if (IsInside(effective, canonicalRoot) && File.Exists(effective))
                return EvalDatasetPathDecision.Allow(effective);
        }

        // One message for "outside every root" and for "inside a root but absent" — see the class remarks.
        return EvalDatasetPathDecision.Refuse("Dataset is not available.");
    }

    /// <summary>
    /// The configured roots that actually confine anything: blank and whitespace-only entries are
    /// dropped rather than honoured.
    /// </summary>
    /// <remarks>
    /// An empty string canonicalises to the process's current directory, so treating one as a root would
    /// quietly widen the allowlist to wherever the host happens to be running from — and would let a
    /// whitespace entry satisfy the fail-closed startup check.
    /// </remarks>
    private static IReadOnlyList<string> UsableRoots(AppConfig config) =>
        [.. (config.AI.Evaluation.DatasetRoots ?? []).Where(root => !string.IsNullOrWhiteSpace(root))];

    /// <summary>
    /// The configured roots, fully link-resolved, reusing the previous answer while the roots are
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roots go through the SAME link resolution as the candidate. Comparing a fully-resolved candidate
    /// against a merely-canonicalised root refuses everything whenever the root is reached through a
    /// symlinked directory — macOS <c>/tmp</c> (a link to <c>/private/tmp</c>) and plenty of container
    /// bind mounts are exactly that. It fails closed, so it is an availability trap rather than a
    /// bypass, but a confinement rule that silently rejects every legitimate path gets switched off by
    /// whoever hits it.
    /// </para>
    /// <para>
    /// <strong>Memoized because the roots do not vary with the candidate.</strong> Resolving them
    /// inside the per-candidate loop repeated the same link walk for every path checked — which was
    /// tolerable when a check meant one dataset, and is not now that the name catalog confines every
    /// file in every root on each listing. Only the resolution is reused; the containment comparison
    /// and the <c>File.Exists</c> still happen per candidate, so nothing about the decision is cached.
    /// </para>
    /// <para>
    /// The cache is keyed on the configured roots themselves, so a configuration reload that changes
    /// them recomputes rather than serving a stale allowlist — which is the direction that matters:
    /// a stale <em>wider</em> allowlist would be a bypass.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> ResolvedRoots(IReadOnlyList<string> roots)
    {
        var cached = _resolvedRoots;
        if (cached is not null && cached.Configured.SequenceEqual(roots, StringComparer.Ordinal))
            return cached.Resolved;

        var resolved = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            try
            {
                var rootWalk = new LinkWalk();
                var canonicalRoot = ResolveSegments(Path.GetFullPath(root), rootWalk);

                // An unresolvable root confines nothing knowable, so drop it rather than compare
                // against a half-resolved path.
                if (!rootWalk.Exhausted)
                    resolved.Add(canonicalRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed root contributes nothing rather than refusing every path.
            }
        }

        // A plain field write, not a lock. Two threads racing here compute the same answer from the
        // same configuration, so the loser's work is wasted rather than wrong, and a torn read is not
        // possible for a reference.
        _resolvedRoots = new RootCache(roots, resolved);
        return resolved;
    }

    /// <summary>The last root resolution, held so an unchanged configuration is not re-walked.</summary>
    private volatile RootCache? _resolvedRoots;

    /// <summary>One resolution of the configured roots, and the configuration it was computed from.</summary>
    private sealed record RootCache(IReadOnlyList<string> Configured, IReadOnlyList<string> Resolved);

    /// <summary>Total symbolic links followed while resolving one path, across every segment.</summary>
    /// <remarks>
    /// A budget rather than a per-segment limit: without one, a chain of links each pointing at a path
    /// containing further links would multiply out instead of terminating.
    /// </remarks>
    private const int MaxLinkHops = 16;

    /// <summary>Mutable state threaded through one path walk: links followed, and whether we ran out.</summary>
    /// <remarks>
    /// <see cref="Exhausted"/> exists so running out of budget is distinguishable from finishing. Both
    /// leave a path behind, but only one of them is a path we actually resolved — and an unresolved link
    /// that happens to sit inside an allowed root would sail through containment.
    /// </remarks>
    private sealed class LinkWalk
    {
        /// <summary>Links followed so far, across every segment of the path.</summary>
        public int Hops { get; set; }

        /// <summary>Whether the walk hit <see cref="MaxLinkHops"/> with a link still unresolved.</summary>
        public bool Exhausted { get; set; }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the file the operating system would actually open, following
    /// symbolic links on <em>every</em> segment: the parent chain first, then the segment itself.
    /// </summary>
    /// <remarks>
    /// .NET has no <c>realpath</c>, and the built-in <c>ResolveLinkTarget</c> only inspects the final
    /// segment. Resolving the leaf alone would leave the obvious evasion open: a <em>directory</em> link
    /// sitting inside an allowed root, pointing anywhere on disk. Every path under it would then
    /// canonicalise to something that starts with the root while opening a file that does not.
    /// </remarks>
    private static string ResolveSegments(string path, LinkWalk walk)
    {
        var parent = Path.GetDirectoryName(path);

        // No parent means a volume root ("C:\", "/"), which terminates the walk.
        if (string.IsNullOrEmpty(parent))
            return path;

        var current = Path.Combine(ResolveSegments(parent, walk), Path.GetFileName(path));

        string? linkTarget;
        try
        {
            // The raw target string, not ResolveLinkTarget's FileSystemInfo: a relative target has to be
            // resolved against the link's own directory, and reconstructing an absolute path from a
            // FileSystemInfo resolves it against the process's current directory instead.
            linkTarget = Directory.Exists(current)
                ? new DirectoryInfo(current).LinkTarget
                : new FileInfo(current).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as escaping. Return what we have; the containment check and the
            // File.Exists probe that follow both fail closed.
            return current;
        }

        if (linkTarget is null)
            return current;

        if (walk.Hops >= MaxLinkHops)
        {
            walk.Exhausted = true;
            return current;
        }

        walk.Hops++;

        // The target may itself sit beneath further links, so it goes through the same walk rather than
        // being trusted as final. This is also what bounds a link cycle: each turn costs a hop.
        string absoluteTarget;
        try
        {
            var linkDirectory = Path.GetDirectoryName(current);
            absoluteTarget = string.IsNullOrEmpty(linkDirectory)
                ? Path.GetFullPath(linkTarget)
                : Path.GetFullPath(linkTarget, linkDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A reparse point whose stored target is malformed. Unresolvable, so treat it exactly like a
            // budget exhaustion: we do not know where this leads, and every other failure mode here
            // refuses rather than guessing. Letting it throw would surface as a 500 instead.
            walk.Exhausted = true;
            return current;
        }

        return ResolveSegments(absoluteTarget, walk);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> sits inside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Compares against the root plus a separator so <c>/data/evals-secret</c> is not treated as living
    /// inside <c>/data/evals</c> — a plain <c>StartsWith</c> on the root would admit it. Comparison is
    /// case-insensitive on Windows and macOS and case-sensitive elsewhere, matching how the filesystem
    /// itself resolves the two paths.
    /// </remarks>
    private static bool IsInside(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedRoot, comparison);
    }
}
