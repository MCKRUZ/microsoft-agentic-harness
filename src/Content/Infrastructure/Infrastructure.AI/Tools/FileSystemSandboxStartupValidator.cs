using Domain.Common.Helpers;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools;

/// <summary>
/// One-shot startup validator for the file-system tool's sandbox. Runs via
/// <see cref="IHostedService.StartAsync"/> and refuses to boot the host when a protected
/// harness-state directory sits inside a directory the file-system tool is allowed to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why containment, and not just the deny list.</b> <see cref="FileSystemService"/> denies the
/// governance-state directory outright, and resolves symlinks, junctions and 8.3 short names before
/// comparing, so no alias of that kind reaches it. Hard links are the gap. A hard link is a second
/// directory entry for the same file rather than a reparse point: it carries no link target,
/// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> returns nothing for it, and its canonical
/// form is itself. A hard link created inside the workspace therefore presents as an ordinary,
/// unprotected file that happens to read and write the approval-verdict database. Creating one
/// needs no privilege on Windows (<c>mklink /H</c>) or Linux (<c>ln</c>).
/// </para>
/// <para>
/// <b>What containment buys.</b> A hard link cannot span volumes — it is an entry in the same
/// filesystem as its target. So if no protected directory lies inside any allowed base path, no
/// hard link inside an allowed base path can name a protected file, whatever the agent does. That
/// makes the allowlist the load-bearing control and demotes the deny list to genuine defence in
/// depth, which is the correct ordering: the allowlist is checked on the target's real identity,
/// the deny list on its spelling.
/// </para>
/// <para>
/// <b>Why this is gated on durable state being enabled.</b> With both durable-state toggles off no
/// database is ever created and the protected directory holds nothing, so refusing to boot would
/// cost a consumer their widened Development allowlist for no security gain. The toggles are
/// documented as restart-required (see <c>GovernanceDurableStateConfig</c>), so enabling one
/// re-runs this validator before the database can be written. An overlap found while the toggles
/// are off is logged as a warning rather than swallowed, because a database left behind by an
/// earlier enabled run is still sitting there.
/// </para>
/// </remarks>
public sealed class FileSystemSandboxStartupValidator : IHostedService
{
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly IOptionsMonitor<AppConfig> _config;
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
    /// <param name="config">Configuration monitor, read for the durable-state toggles.</param>
    /// <param name="logger">Logger for the validation outcome.</param>
    public FileSystemSandboxStartupValidator(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths,
        IOptionsMonitor<AppConfig> config,
        ILogger<FileSystemSandboxStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(allowedBasePaths);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _allowedBasePaths = allowedBasePaths;
        _protectedPaths = protectedPaths;
        _logger = logger;
        _config = config;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A protected harness-state directory lies inside an allowed base path while durable
    /// governance state is enabled.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var overlaps = FindOverlaps();
        if (overlaps.Count == 0)
            return Task.CompletedTask;

        var durableState = _config.CurrentValue.AI.Governance.DurableState;
        if (!durableState.EscalationsEnabled && !durableState.ChangeProposalsEnabled)
        {
            _logger.LogWarning(
                "Protected harness-state directory {ProtectedPath} lies inside the file-system tool's " +
                "allowed base path {AllowedBasePath}. Durable governance state is currently disabled, so " +
                "the host is allowed to start, but enabling AppConfig:AI:Governance:DurableState will " +
                "refuse to boot until the allowlist is narrowed.",
                overlaps[0].ProtectedPath,
                overlaps[0].AllowedBasePath);

            return Task.CompletedTask;
        }

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
            $"Durable governance state is enabled, but {overlaps.Count} protected harness-state " +
            $"director{(overlaps.Count == 1 ? "y lies" : "ies lie")} inside a directory the file-system " +
            $"tool is allowed to reach: {detail}. That directory holds the SQLite database of approval " +
            "verdicts. The deny list alone cannot protect it there: an agent can create a hard link to " +
            "the database inside the allowed tree, and a hard link is not a reparse point, so it " +
            "resolves to itself and reads as an ordinary unprotected file — through which the database " +
            "can be read and rewritten to forge an approval verdict. " +
            "Fix: narrow AppConfig:Infrastructure:FileSystem:AllowedBasePaths so it no longer contains " +
            "the protected directory (the shipped default, 'workspace', already satisfies this). " +
            "Moving AppConfig:AI:Governance:DurableState:DatabasePath is not an alternative on its own: " +
            "that path is required to resolve under the application base directory, so an allowlist " +
            "that covers the application base directory will always contain it.";
    }
}
