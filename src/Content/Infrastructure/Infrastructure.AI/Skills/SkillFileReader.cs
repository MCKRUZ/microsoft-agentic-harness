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
public sealed class SkillFileReader : ISkillFileReader
{
    // Matched to FileSystemService deliberately: a manifest or reference file large enough to
    // exhaust memory is a defect or an attack either way, and two different limits on two sandboxes
    // over the same disk would be a difference nobody could justify later.
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

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
    public string ReadText(string path)
    {
        var fullPath = ValidateForRead(path);
        return File.ReadAllText(fullPath);
    }

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateForRead(path);
        return await File.ReadAllTextAsync(fullPath, System.Text.Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(Guard().ResolveAndValidate(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(Guard().ResolveAndValidate(path));

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var guard = Guard();
        var fullPath = guard.ResolveAndValidate(path);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<string>();
        foreach (var subdirectory in Directory.EnumerateDirectories(fullPath))
        {
            // Re-checked rather than trusted because it was enumerated from inside the sandbox: a
            // junction planted in a skill root enumerates with a legitimate-looking literal path,
            // and a caller that recursed into it would be walking wherever it points.
            if (guard.IsPathAllowed(subdirectory))
                results.Add(subdirectory);
            else
                _logger.LogWarning("Skipped skill subdirectory outside the skill sandbox: {Path}", subdirectory);
        }

        return results;
    }

    /// <summary>
    /// Validates a path for reading and enforces the size limit before any content is loaded.
    /// </summary>
    private string ValidateForRead(string path)
    {
        var fullPath = Guard().ResolveAndValidate(path);

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Skill file not found: {path}");

        if (fileInfo.Length > MaxFileSizeBytes)
            throw new IOException($"Skill file exceeds size limit ({MaxFileSizeBytes / 1024 / 1024} MB).");

        return fullPath;
    }

    /// <summary>
    /// Returns a guard over the currently configured skill content roots, rebuilding it only when
    /// that set has changed since the last call.
    /// </summary>
    private SandboxedPathGuard Guard()
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

            if (!guard.HasAllowedPaths)
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
    /// The directories skill content may be read from: the configured skill roots, the configured
    /// agent roots (an agent's own skills live in <c>&lt;agentDir&gt;/skills</c>), and the bundle
    /// staging root (a staged bundle carries its own <c>skills/</c> directory).
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against <see cref="AppContext.BaseDirectory"/>, matching the registries
    /// and <c>BundleStagingService</c>. Resolving them any other way here would produce a sandbox
    /// that refuses the very directories discovery then walks.
    /// </remarks>
    private IReadOnlyList<string> ResolveContentRoots()
    {
        var ai = _appConfig.CurrentValue.AI;
        if (ai is null)
            return [];

        var roots = new List<string>();

        foreach (var path in ai.Skills.AllPaths.Concat(ai.Agents.AllPaths))
        {
            if (!string.IsNullOrWhiteSpace(path))
                roots.Add(Resolve(path));
        }

        roots.Add(string.IsNullOrWhiteSpace(ai.BundleExecution.TempRoot)
            ? Path.Combine(Path.GetTempPath(), "agent-bundles")
            : Resolve(ai.BundleExecution.TempRoot));

        return [.. roots.Distinct(StringComparer.Ordinal)];

        static string Resolve(string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppContext.BaseDirectory);
    }
}
