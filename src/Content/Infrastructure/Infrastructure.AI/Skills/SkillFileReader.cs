using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Confines every skill-content read to the configured skill roots, using the same allow/deny rules
/// as the model's file sandbox but a different — and read-only — set of permitted directories.
/// </summary>
/// <remarks>
/// <para>
/// See <see cref="ISkillFileReader"/> for why skill loading has its own sandbox instead of being
/// added to <see cref="FileSystemService"/>'s, and <see cref="SandboxedPathGuard"/> for the rules
/// both share.
/// </para>
/// <para>
/// <b>The permitted set is recomputed, not snapshotted.</b> Plugin skill directories are appended to
/// <c>AppConfig.AI.Skills.AdditionalPaths</c> by <c>PluginStartupLoader</c> during host start —
/// after the DI container is built, and therefore after any set captured at registration time. A
/// snapshot would refuse every plugin-supplied skill. Each call compares a cheap signature of the
/// configured roots against the cached one and rebuilds the guard only when they differ, so the
/// per-call cost is a string join rather than the handle-opening canonicalization of every root.
/// </para>
/// <para>
/// <b>This does not let a plugin widen the sandbox arbitrarily.</b> A plugin's skill directory is
/// accepted only after <c>PluginLoader</c> has verified it is contained within the plugin's own
/// directory, and that directory comes from <c>AppConfig:AI:Plugins:Packages</c> — operator
/// configuration, not plugin-supplied content.
/// </para>
/// </remarks>
public sealed class SkillFileReader : SandboxedSkillFileReaderBase
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<SkillFileReader> _logger;
    private readonly Lock _lock = new();

    private string? _cachedSignature;
    private SandboxedPathGuard? _cachedGuard;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillFileReader"/> class.
    /// </summary>
    /// <param name="appConfig">Monitor over the live configuration supplying the skill content roots.</param>
    /// <param name="logger">Logger for sandbox refusals.</param>
    public SkillFileReader(IOptionsMonitor<AppConfig> appConfig, ILogger<SkillFileReader> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string OutsideSandboxMessage(string path) =>
        $"Path is outside the configured skill content roots: {path}";

    /// <inheritdoc />
    protected override void LogSkippedSubdirectory(string subdirectory) =>
        _logger.LogWarning("Skipped skill subdirectory outside the skill sandbox: {Path}", subdirectory);

    /// <summary>
    /// Returns a guard over the currently configured skill content roots, rebuilding it only when
    /// that set has changed since the last call.
    /// </summary>
    private protected override SandboxedPathGuard CurrentGuard()
    {
        var roots = ResolveContentRoots();
        var signature = string.Join('\0', roots);

        lock (_lock)
        {
            if (_cachedGuard is not null && string.Equals(_cachedSignature, signature, StringComparison.Ordinal))
                return _cachedGuard;

            // No protected paths: this sandbox permits only skill content roots, and the bundle
            // staging service already refuses a staging root that overlaps a discovery root. The
            // governance-state directory is guarded on the surface that can write to it.
            var guard = new SandboxedPathGuard(_logger, roots);

            if (guard.AllowedPathCount == 0)
            {
                _logger.LogWarning(
                    "SkillFileReader initialized with zero skill content roots — all skill reads will be denied");
            }
            else
            {
                _logger.LogDebug(
                    "SkillFileReader sandbox covering {PathCount} skill content root(s)", guard.AllowedPathCount);
            }

            _cachedSignature = signature;
            _cachedGuard = guard;
            return guard;
        }
    }

    /// <summary>
    /// Every directory skill content may be read from, resolved live from configuration.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SkillContentRoots"/> rather than resolving roots itself: the same
    /// answer is needed by the registries that walk these directories and by the staging service
    /// that refuses a staging root overlapping them, and a sandbox that resolved a root differently
    /// would refuse the very directory discovery then walks.
    /// </remarks>
    private IReadOnlyList<string> ResolveContentRoots() =>
        SkillContentRoots.All(_appConfig.CurrentValue);
}
