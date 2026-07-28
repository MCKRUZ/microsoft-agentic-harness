using System.Runtime.InteropServices;
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
/// <para>
/// <b>It also refuses a platform the hard-link control cannot run on.</b> That control is what
/// actually closes the bypass, and it fails closed, so on an unimplemented platform — macOS and the
/// BSDs — every file operation is denied. Without this check the operator meets that as a stream of
/// per-call refusals whose message cannot name the real cause, since at the point of denial the
/// service knows only that the count was unavailable. Meeting it once, at boot, with the platform
/// named and the ways forward spelled out, is the difference between a documented limitation and an
/// inexplicably broken template.
/// </para>
/// </remarks>
public sealed class FileSystemSandboxStartupValidator : IHostedService
{
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly ILogger<FileSystemSandboxStartupValidator> _logger;
    private readonly bool _hardLinkInspectionSupported;

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
        : this(allowedBasePaths, protectedPaths, logger, HardLinkInspector.IsSupportedPlatform)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="FileSystemSandboxStartupValidator"/> with the hard-link
    /// platform-capability signal supplied explicitly.
    /// </summary>
    /// <remarks>
    /// Exists so the platform assertion can be tested on every operating system CI runs on. Faking
    /// the OS itself is not possible in-process, and a test that could only fail on macOS would
    /// never run — the repository's CI is Linux-only. Passing the capability in means the branch is
    /// exercised everywhere, which is the difference between a covered guard and one nobody has
    /// watched fail.
    /// </remarks>
    /// <param name="allowedBasePaths">The base paths the file-system tool may reach, as registered.</param>
    /// <param name="protectedPaths">The harness-state directories denied to the tool, as registered.</param>
    /// <param name="logger">Logger for the validation outcome.</param>
    /// <param name="hardLinkInspectionSupported">
    /// Whether the running platform has a link-count implementation. Production passes
    /// <see cref="HardLinkInspector.IsSupportedPlatform"/>.
    /// </param>
    internal FileSystemSandboxStartupValidator(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths,
        ILogger<FileSystemSandboxStartupValidator> logger,
        bool hardLinkInspectionSupported)
    {
        ArgumentNullException.ThrowIfNull(allowedBasePaths);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _allowedBasePaths = allowedBasePaths;
        _protectedPaths = protectedPaths;
        _logger = logger;
        _hardLinkInspectionSupported = hardLinkInspectionSupported;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A protected harness-state directory lies inside an allowed base path, or protected
    /// directories are configured on a platform where the hard-link control cannot run.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Geometry first. When both are wrong, the overlap is the one that is a security
        // misconfiguration in its own right and stays wrong after a move to a supported platform,
        // so it is the more useful thing to say first.
        var overlaps = FindOverlaps();
        if (overlaps.Count > 0)
        {
            // Logged structurally as well as thrown: the exception message is one string for a
            // human, while these fields are what a log query can actually group and alert on.
            _logger.LogError(
                "Refusing to start: protected harness-state directory {ProtectedPath} lies inside the " +
                "file-system tool's allowed base path {AllowedBasePath} ({OverlapCount} overlap(s) total).",
                overlaps[0].ProtectedPath,
                overlaps[0].AllowedBasePath,
                overlaps.Count);

            throw new InvalidOperationException(BuildOverlapFailureMessage(overlaps));
        }

        if (HasProtectedPaths && !_hardLinkInspectionSupported)
        {
            _logger.LogError(
                "Refusing to start: the file-system tool's hard-link sandbox control has no " +
                "implementation on {Platform}, and {ProtectedPathCount} protected harness-state " +
                "director(y/ies) are configured, so every file operation would be denied.",
                PlatformLabel,
                _protectedPaths.Count);

            throw new InvalidOperationException(BuildUnsupportedPlatformMessage());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether any usable protected path is configured — the condition under which the file-system
    /// tool arms its per-operation hard-link check.
    /// </summary>
    /// <remarks>
    /// Blank entries are discarded to mirror <see cref="FileSystemService"/> exactly: its
    /// constructor drops them before counting, so a list holding only blanks leaves the runtime
    /// check disarmed. A validator that counted raw entries would refuse to boot over a
    /// configuration the tool treats as having nothing to protect.
    /// </remarks>
    private bool HasProtectedPaths =>
        _protectedPaths.Any(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>
    /// A human-readable name for the running operating system.
    /// </summary>
    /// <remarks>
    /// macOS is spelled out because it is the platform this refusal will overwhelmingly be read on,
    /// and <see cref="RuntimeInformation.OSDescription"/> reports it as "Darwin" plus a kernel
    /// version, which reads as an unrelated system to someone who has not met it before.
    /// </remarks>
    private static string PlatformLabel =>
        OperatingSystem.IsMacOS()
            ? $"macOS ({RuntimeInformation.OSDescription})"
            : RuntimeInformation.OSDescription;

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
    /// Builds the boot-failure message for the unimplemented-platform case: what is unavailable,
    /// what it would cost to ignore, and the only two things that actually resolve it.
    /// </summary>
    /// <remarks>
    /// The message deliberately forecloses the fix an operator would otherwise try first. Turning
    /// the durable-governance toggles off looks like it should empty the protected list, and it does
    /// not: <c>RegisterToolServices</c> derives the governance-state directory from
    /// <c>GovernanceStatePaths.Resolve</c> unconditionally, consulting no toggle, and the configured
    /// database path defaults to a non-blank value. Sending an operator down that path would cost
    /// them an hour and end where they started.
    /// </remarks>
    private string BuildUnsupportedPlatformMessage()
    {
        var firstProtected = _protectedPaths.First(p => !string.IsNullOrWhiteSpace(p));

        return
            "The file-system tool's hard-link sandbox control has no implementation on this " +
            $"platform ({PlatformLabel}), and protected harness-state directories are configured " +
            $"(for example '{firstProtected}'). That control asks the operating system how many " +
            "directory entries name a file, which is the only question that detects a hard link " +
            "aliasing protected state; it is implemented with GetFileInformationByHandle on Windows " +
            "and statx on Linux, and it fails closed everywhere else. Booting anyway would deny " +
            "every read, write, and search the file-system tool attempts, one call at a time, with " +
            "an error that cannot name this as the cause. " +
            "Two ways forward: (1) run the harness on Windows or Linux, the supported platforms; " +
            "(2) add a branch for this platform to HardLinkInspector — macOS and the BSDs expose the " +
            "link count only through struct stat, whose field order and widths vary by operating " +
            "system AND processor architecture, so the layout must be verified against the target " +
            "rather than assumed; a wrong offset reads the wrong bytes and fails open silently. " +
            "Note what will NOT work: turning the AppConfig:AI:Governance:DurableState toggles off " +
            "does not remove the protected paths. The governance-state directory is registered as " +
            "protected unconditionally, whatever those toggles say, so the file-system tool is " +
            "unusable on this platform until one of the two options above is taken.";
    }

    /// <summary>
    /// Builds the boot-failure message: what is wrong, why it matters, and which setting to change.
    /// </summary>
    /// <param name="overlaps">The overlaps found; never empty.</param>
    private static string BuildOverlapFailureMessage(IReadOnlyList<SandboxOverlap> overlaps)
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
