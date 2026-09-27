using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
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
public sealed class MaterializedSkillDirectoryFileReader : ISkillFileReader
{
    // Matched to SkillFileReader deliberately (#618 follow-up finding on the abandoned first
    // attempt at this fix): a manifest large enough to exhaust memory is a defect or an attack
    // either way, and an eval-only reader is not exempt just because its content is temporary.
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

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
    public string ReadText(string path) => File.ReadAllText(ValidateForRead(path));

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(ValidateForRead(path), System.Text.Encoding.UTF8, cancellationToken);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(Resolve(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var fullPath = Resolve(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<string>();
        foreach (var subdirectory in Directory.EnumerateDirectories(fullPath))
        {
            // Logged, matching SkillFileReader.EnumerateDirectories' own rejection path — silently
            // dropping this would make "why didn't the candidate's sibling skill load" unnecessarily
            // hard to diagnose (code-review finding).
            if (_guard.IsPathAllowed(subdirectory))
                results.Add(subdirectory);
            else
                _logger.LogWarning(
                    "Skipped materialized skill subdirectory outside the eval sandbox: {Path}", subdirectory);
        }

        return results;
    }

    /// <summary>
    /// Validates a path against the sandbox, translating a refusal to
    /// <see cref="SkillPathRefusedException"/> — see <c>SkillFileReader.Resolve</c>'s remarks for
    /// why that translation matters (a genuine permission denial on a permitted path must not be
    /// confused with a sandbox refusal).
    /// </summary>
    private string Resolve(string path)
    {
        try
        {
            return _guard.ResolveAndValidate(path);
        }
        catch (UnauthorizedAccessException ex) when (ex is not SkillPathRefusedException)
        {
            throw new SkillPathRefusedException(
                $"Path is outside the materialized candidate skill directory: {path}", ex);
        }
    }

    private string ValidateForRead(string path)
    {
        var fullPath = Resolve(path);

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Skill file not found: {path}");

        if (fileInfo.Length > MaxFileSizeBytes)
            throw new IOException($"Skill file exceeds size limit ({MaxFileSizeBytes / 1024 / 1024} MB).");

        return fullPath;
    }
}
