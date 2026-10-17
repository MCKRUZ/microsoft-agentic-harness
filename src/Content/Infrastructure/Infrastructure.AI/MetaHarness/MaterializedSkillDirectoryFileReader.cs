using Infrastructure.AI.Skills;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Confines every read to one eval run's materialized candidate-skill directory — the eval-only
/// counterpart of <c>Infrastructure.AI.Skills.SkillFileReader</c>, which confines reads to the
/// configured, permanent skill content roots (#247). A candidate's materialized temp directory is
/// never one of those configured roots, and must never become one: it is per-execution-run,
/// ephemeral, and its content is untrusted (a proposed skill mutation), unlike the harness's own
/// installed skills.
/// </summary>
/// <remarks>
/// One instance is scoped to exactly one eval task's materialized root — unlike
/// <c>SkillFileReader</c>, whose permitted set is recomputed on every call because plugin
/// directories can be appended after startup, this reader's single root is fixed for its whole
/// lifetime, so a plain immutable <see cref="SandboxedPathGuard"/> built once at construction is
/// sufficient.
/// </remarks>
public sealed class MaterializedSkillDirectoryFileReader : SandboxedSkillFileReaderBase
{
    private readonly SandboxedPathGuard _guard;
    private readonly ILogger _logger;

    /// <param name="root">The single materialized directory this reader may read from.</param>
    /// <param name="logger">Receives a warning for every sandbox refusal.</param>
    public MaterializedSkillDirectoryFileReader(string root, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(logger);

        _guard = new SandboxedPathGuard(logger, [root]);
        _logger = logger;
    }

    /// <inheritdoc />
    private protected override SandboxedPathGuard CurrentGuard() => _guard;

    /// <inheritdoc />
    protected override string OutsideSandboxMessage(string path) =>
        $"Path is outside the materialized candidate skill directory: {path}";

    /// <inheritdoc />
    protected override void LogSkippedSubdirectory(string subdirectory) =>
        _logger.LogWarning(
            "Skipped materialized skill subdirectory outside the eval sandbox: {Path}", subdirectory);
}
