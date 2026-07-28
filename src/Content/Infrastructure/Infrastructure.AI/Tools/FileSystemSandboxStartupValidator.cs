using Domain.Common.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// One-shot startup validator for the file-system tool's sandbox. Runs via
/// <see cref="IHostedService.StartAsync"/> and refuses to boot the host when a protected
/// harness-state directory sits inside a directory the file-system tool is allowed to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is, and what it is not.</b> Granting the file-system tool a directory that
/// contains the harness's own governance-state database is a misconfiguration on its face: it
/// leaves the deny list as the only thing standing between an agent and the approval-verdict
/// store, with every alias trick (8.3 short names, symlinks, junctioned parents) aimed straight at
/// it. Refusing to boot on that geometry is worth doing and this validator does it.
/// </para>
/// <para>
/// <b>It is not the hard-link control, and an earlier version of this file wrongly claimed to be.</b>
/// The claim was that a hard link cannot span volumes, so a protected directory outside every
/// allowed base path cannot be hard-linked into one. The premise is true and the conclusion does
/// not follow: a hard link requires the same VOLUME as its target, not the same SUBTREE.
/// Non-containment is strictly narrower than volume separation, so eliminating containment
/// eliminates nothing — and the shipped default, <c>workspace</c> and <c>.agent-state</c> as
/// siblings under the application base directory, is same-volume by construction. Volume
/// separation is not reachable by configuration either, because <c>GovernanceStatePaths.Resolve</c>
/// pins the database under the application base directory. The control that actually closes the
/// hard-link hole is <see cref="FileSystemService"/>'s per-operation identity check, which asks the
/// operating system how many directory entries name the file rather than what the path spells.
/// This validator is defence in depth alongside it.
/// </para>
/// <para>
/// <b>Why it refuses unconditionally.</b> An earlier version downgraded the refusal to a warning
/// while both durable-state toggles were off, on the reasoning that the toggles are documented as
/// restart-required so enabling one would re-run this validator before any database could be
/// written. That does not hold. <c>EscalationReconciliationService.RunPruneAsync</c> re-reads
/// <c>AI:Governance:DurableState</c> from <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// on every reconcile tick, and <c>AppConfigHelper</c> loads appsettings with
/// <c>reloadOnChange: true</c>, so an operator who edits <c>ChangeProposalsEnabled</c> to true on a
/// running host resolves the pruner factory — and with it the schema initializer that creates the
/// database file — on a host where this validator already warned and moved on. A boot-time
/// assertion that a live configuration change can invalidate has to hold on every boot.
/// </para>
/// </remarks>
public sealed class FileSystemSandboxStartupValidator : IHostedService
{
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly ILogger<FileSystemSandboxStartupValidator> _logger;

    /// <summary>
    /// Initializes a new <see cref="FileSystemSandboxStartupValidator"/>.
    /// </summary>
    /// <remarks>
    /// The two path collections are passed in rather than re-derived from configuration so that
    /// this validator asserts against the exact values handed to <see cref="FileSystemService"/>.
    /// Re-deriving them would create a second copy of the resolution rules, and a validator that
    /// checks a slightly different set of paths from the one actually enforced is worse than none.
    /// </remarks>
    /// <param name="allowedBasePaths">The base paths the file-system tool may reach, as registered.</param>
    /// <param name="protectedPaths">The harness-state directories denied to the tool, as registered.</param>
    /// <param name="logger">Logger for the validation outcome.</param>
    public FileSystemSandboxStartupValidator(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths,
        ILogger<FileSystemSandboxStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(allowedBasePaths);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _allowedBasePaths = allowedBasePaths;
        _protectedPaths = protectedPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A protected harness-state directory lies inside an allowed base path.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var overlaps = FindOverlaps();
        if (overlaps.Count == 0)
            return Task.CompletedTask;

        // Logged structurally as well as thrown: the exception message is one string for a human,
        // while these fields are what a log query can actually group and alert on.
        _logger.LogError(
            "Refusing to start: protected harness-state directory {ProtectedPath} lies inside the " +
            "file-system tool's allowed base path {AllowedBasePath} ({OverlapCount} overlap(s) total).",
            overlaps[0].ProtectedPath,
            overlaps[0].AllowedBasePath,
            overlaps.Count);

        throw new InvalidOperationException(BuildFailureMessage(overlaps));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// One protected directory found inside one allowed base path.
    /// </summary>
    /// <param name="ProtectedPath">The canonical protected directory.</param>
    /// <param name="AllowedBasePath">The canonical allowed base path containing it.</param>
    private readonly record struct SandboxOverlap(string ProtectedPath, string AllowedBasePath);

    /// <summary>
    /// Returns every (protected path, allowed base path) pair where the former is the same as, or
    /// nested under, the latter.
    /// </summary>
    /// <remarks>
    /// Both sides are canonicalized first, using the same resolver <see cref="FileSystemService"/>
    /// applies at runtime, so an overlap reached through a symlinked or 8.3-aliased parent is
    /// still detected. Only this direction is checked: a protected directory <em>containing</em>
    /// an allowed base path is incoherent but not exploitable, since the deny list wins over the
    /// allowlist and every path under it is refused anyway.
    /// </remarks>
    private List<SandboxOverlap> FindOverlaps()
    {
        var canonicalBases = _allowedBasePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => SandboxPathCanonicalizer.Canonicalize(PathScope.Normalize(p)))
            .ToList();

        return (from protectedPath in _protectedPaths
                where !string.IsNullOrWhiteSpace(protectedPath)
                let canonicalProtected =
                    SandboxPathCanonicalizer.Canonicalize(PathScope.Normalize(protectedPath))
                from basePath in canonicalBases
                where PathScope.IsSameOrUnderNormalized(canonicalProtected, basePath)
                select new SandboxOverlap(canonicalProtected, basePath))
            .ToList();
    }

    /// <summary>
    /// Builds the boot-failure message: what is wrong, why it matters, and which setting to change.
    /// </summary>
    /// <param name="overlaps">The overlaps found; never empty.</param>
    private static string BuildFailureMessage(IReadOnlyList<SandboxOverlap> overlaps)
    {
        var detail = string.Join(
            "; ",
            overlaps.Select(o => $"'{o.ProtectedPath}' inside allowed base path '{o.AllowedBasePath}'"));

        return
            $"{overlaps.Count} protected harness-state " +
            $"director{(overlaps.Count == 1 ? "y lies" : "ies lie")} inside a directory the file-system " +
            $"tool is allowed to reach: {detail}. That directory holds the SQLite database of approval " +
            "verdicts and change proposals. Reaching it lets an agent mine the approval payloads it " +
            "carries, and truncate or corrupt the harness's own governance state; forging a verdict is " +
            "separately blocked by the HMAC seal on every row. Granting the tool that directory leaves " +
            "the deny list as the only barrier, and a deny list compares paths. " +
            "Fix: narrow AppConfig:Infrastructure:FileSystem:AllowedBasePaths so it no longer contains " +
            "the protected directory (the shipped default, 'workspace', already satisfies this). " +
            "Moving AppConfig:AI:Governance:DurableState:DatabasePath is not an alternative on its own: " +
            "that path is required to resolve under the application base directory, so an allowlist " +
            "that covers the application base directory will always contain it.";
    }
}
