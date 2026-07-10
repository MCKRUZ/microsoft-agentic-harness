using System.IO.Compression;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Bundles;
using Domain.AI.Skills;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.Plugins;
using Domain.Common.Helpers;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// Default <see cref="IBundleStagingService"/>. Validates a received zip against the configured archive
/// limits, extracts it into an isolated per-bundle directory under hostile-input guards (zip-slip,
/// decompression bomb, escaping symlinks, staging/discovery-root disjointness), then reuses the host's
/// ordinary <c>AGENT.md</c>/<c>SKILL.md</c>/<c>plugin.json</c> parsers to produce a <see cref="StagedBundle"/>.
/// </summary>
/// <remarks>
/// Nothing extracted from the archive is parsed until every structural guard has passed, and any guard
/// failure deletes the partial extraction before returning. The service never surfaces archive content
/// in a failure reason — reasons describe the guard, not the payload.
/// </remarks>
public sealed class BundleStagingService : IBundleStagingService
{
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Compression-ratio checking is skipped below this uncompressed size. Small, highly-compressible
    /// text bundles (a handful of markdown files) can legitimately exceed the ratio; the absolute
    /// <see cref="BundleExecutionConfig.MaxTotalUncompressedBytes"/> guard already bounds them. The ratio
    /// guard exists for the large-payload case where a bomb's signature is a runaway expansion factor.
    /// </summary>
    private const long RatioGuardFloorBytes = 1024 * 1024;

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly AgentMetadataParser _agentParser;
    private readonly SkillMetadataParser _skillParser;
    private readonly IPluginManifestReader _pluginReader;
    private readonly ILogger<BundleStagingService> _logger;

    /// <summary>Initialises the staging service with its parsers, configuration, and logger.</summary>
    public BundleStagingService(
        IOptionsMonitor<AppConfig> appConfig,
        AgentMetadataParser agentParser,
        SkillMetadataParser skillParser,
        IPluginManifestReader pluginReader,
        ILogger<BundleStagingService> logger)
    {
        _appConfig = appConfig;
        _agentParser = agentParser;
        _skillParser = skillParser;
        _pluginReader = pluginReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<StagedBundle>> StageAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var cfg = _appConfig.CurrentValue.AI.BundleExecution;

        var stagingRoot = ResolveStagingRoot(cfg);
        var disjoint = ValidateStagingRootDisjoint(stagingRoot);
        if (!disjoint.IsSuccess)
            return Result<StagedBundle>.Fail([.. disjoint.Errors]);

        var buffered = await BufferArchiveAsync(archive, cfg, cancellationToken);
        if (!buffered.IsSuccess)
            return Result<StagedBundle>.Fail([.. buffered.Errors]);

        using var buffer = buffered.Value!.Stream;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return Result<StagedBundle>.Fail("Bundle is not a valid zip archive.");
        }

        using (zip)
            return await StageOpenedArchiveAsync(zip, buffered.Value!.CompressedLength, cfg, stagingRoot, cancellationToken);
    }

    /// <summary>
    /// Validates the opened archive's shape, extracts it into a fresh per-bundle directory under the
    /// hostile-input guards, and parses the result. Guarantees the staging directory is deleted on every
    /// non-success exit — a guard failure, an unexpected error, or cancellation — so a partial extraction
    /// never survives.
    /// </summary>
    private async Task<Result<StagedBundle>> StageOpenedArchiveAsync(
        ZipArchive zip, long compressedLength, BundleExecutionConfig cfg, string stagingRoot, CancellationToken cancellationToken)
    {
        var structural = ValidateArchiveShape(zip, compressedLength, cfg);
        if (!structural.IsSuccess)
            return Result<StagedBundle>.Fail([.. structural.Errors]);

        var bundleId = $"bundle-{Guid.NewGuid():N}";
        var bundleDir = Path.Combine(stagingRoot, bundleId);
        Directory.CreateDirectory(bundleDir);

        try
        {
            var extract = await ExtractWithGuardsAsync(zip, bundleDir, cfg, compressedLength, cancellationToken);
            if (!extract.IsSuccess)
                return CleanupAndFail(bundleDir, [.. extract.Errors]);

            var symlinks = ValidateNoEscapingSymlinks(bundleDir);
            if (!symlinks.IsSuccess)
                return CleanupAndFail(bundleDir, [.. symlinks.Errors]);

            return ParseStagedBundle(bundleDir, bundleId);
        }
        catch (OperationCanceledException)
        {
            // Honour cancellation, but never leave the partial extraction behind.
            TryCleanup(bundleDir);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bundle staging failed while extracting into {BundleDir}", bundleDir);
            return CleanupAndFail(bundleDir, "Bundle staging failed while extracting the archive.");
        }
    }

    private static string ResolveStagingRoot(BundleExecutionConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.TempRoot)
            ? Path.Combine(Path.GetTempPath(), "agent-bundles")
            : ResolveConfiguredPath(cfg.TempRoot);

    /// <summary>
    /// Resolves a configured path to an absolute one, relative paths being taken against
    /// <see cref="AppContext.BaseDirectory"/> (the bin folder) to match the registries. Used for both the
    /// staging root and the discovery roots so the disjointness guard compares like against like — a
    /// difference here could let an overlapping root slip past.
    /// </summary>
    private static string ResolveConfiguredPath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>
    /// Rejects a staging root that overlaps any configured skill or agent discovery root. The global
    /// registries scan those roots recursively and are bundle-unaware, so a staging root nested under
    /// (or an ancestor of) a discovery root would let them independently discover — and globally publish
    /// — a bundle's skills, defeating the per-run isolation.
    /// </summary>
    private Result ValidateStagingRootDisjoint(string stagingRoot)
    {
        var normalizedStaging = PathScope.Normalize(stagingRoot);
        foreach (var root in ConfiguredDiscoveryRoots())
        {
            var normalizedRoot = PathScope.Normalize(root);
            if (PathScope.IsSameOrUnderNormalized(normalizedStaging, normalizedRoot)
                || PathScope.IsSameOrUnderNormalized(normalizedRoot, normalizedStaging))
            {
                _logger.LogError(
                    "Bundle staging root {StagingRoot} overlaps discovery root {DiscoveryRoot}; refusing to stage",
                    normalizedStaging, normalizedRoot);
                return Result.Fail(
                    "Bundle staging root overlaps a configured skill or agent discovery root. " +
                    "Configure AI:BundleExecution:TempRoot to a location outside the skill and agent paths.");
            }
        }

        return Result.Success();
    }

    private IEnumerable<string> ConfiguredDiscoveryRoots()
    {
        var ai = _appConfig.CurrentValue.AI;
        foreach (var p in ai.Skills.AllPaths.Concat(ai.Agents.AllPaths))
            yield return ResolveConfiguredPath(p);
    }

    private async Task<Result<BufferedArchive>> BufferArchiveAsync(
        Stream archive, BundleExecutionConfig cfg, CancellationToken cancellationToken)
    {
        // The buffered stream is handed to the caller (which owns its disposal and reads the zip directly
        // from it); on any failure path here we dispose it ourselves before returning.
        var sink = new MemoryStream();
        try
        {
            var chunk = new byte[CopyBufferSize];
            long total = 0;
            int read;
            while ((read = await archive.ReadAsync(chunk, cancellationToken)) > 0)
            {
                total += read;
                if (total > cfg.MaxArchiveBytes)
                {
                    sink.Dispose();
                    return Result<BufferedArchive>.Fail(
                        $"Bundle archive exceeds the maximum accepted size of {cfg.MaxArchiveBytes} bytes.");
                }

                sink.Write(chunk, 0, read);
            }

            if (total == 0)
            {
                sink.Dispose();
                return Result<BufferedArchive>.Fail("Bundle archive is empty.");
            }

            sink.Position = 0;
            return Result<BufferedArchive>.Success(new BufferedArchive(sink, total));
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The archive read fully into a seekable in-memory stream, with the compressed length observed
    /// while reading. The <see cref="Stream"/> is positioned at zero and owned by the caller.
    /// </summary>
    private sealed record BufferedArchive(MemoryStream Stream, long CompressedLength);

    /// <summary>
    /// Structural guards read from the archive's central directory before any bytes are written to disk:
    /// entry count, declared total uncompressed size, and (for large payloads) the compression ratio.
    /// The declared sizes here are attacker-controllable header values, so extraction re-checks the
    /// running total against actual bytes written — this pass is the cheap first rejection.
    /// </summary>
    private static Result ValidateArchiveShape(ZipArchive zip, long compressedLength, BundleExecutionConfig cfg)
    {
        if (zip.Entries.Count > cfg.MaxEntryCount)
            return Result.Fail($"Bundle archive has more than the maximum {cfg.MaxEntryCount} entries.");

        long declaredUncompressed = 0;
        foreach (var entry in zip.Entries)
            declaredUncompressed += entry.Length;

        var expansion = CheckExpansionLimits(declaredUncompressed, compressedLength, cfg);
        return expansion is not null ? Result.Fail(expansion) : Result.Success();
    }

    /// <summary>
    /// The shared decompression-bomb predicate: rejects when the uncompressed size (declared at pre-pass
    /// time, actual at extraction time) exceeds the absolute cap, or when it exceeds the ratio limit above
    /// the small-payload floor. Kept in one place so the cheap declared-size pass and the authoritative
    /// per-chunk actual-size check can never drift apart. Returns a caller-facing failure reason, or null
    /// when the bytes are within limits.
    /// </summary>
    private static string? CheckExpansionLimits(long uncompressedBytes, long compressedLength, BundleExecutionConfig cfg)
    {
        if (uncompressedBytes > cfg.MaxTotalUncompressedBytes)
            return $"Bundle archive expands to more than the maximum {cfg.MaxTotalUncompressedBytes} bytes.";

        if (uncompressedBytes > RatioGuardFloorBytes
            && compressedLength > 0
            && (double)uncompressedBytes / compressedLength > cfg.MaxCompressionRatio)
            return $"Bundle archive compression ratio exceeds the maximum {cfg.MaxCompressionRatio}.";

        return null;
    }

    private async Task<Result> ExtractWithGuardsAsync(
        ZipArchive zip, string bundleDir, BundleExecutionConfig cfg, long compressedLength, CancellationToken cancellationToken)
    {
        var normalizedBundleDir = PathScope.Normalize(bundleDir);
        var chunk = new byte[CopyBufferSize];
        long actualUncompressed = 0;

        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(bundleDir, entry.FullName));

            // Zip-slip: every entry must resolve to a path inside the bundle directory. Catches "../"
            // traversal and absolute paths (Path.Combine returns a rooted second argument verbatim).
            if (!PathScope.IsSameOrUnderNormalized(PathScope.Normalize(destination), normalizedBundleDir))
                return Result.Fail("Bundle archive contains an entry that escapes the staging directory.");

            // Directory entry (name is empty when the full name ends in a separator).
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

            int read;
            while ((read = await entryStream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                actualUncompressed += read;

                // Bomb guard on ACTUAL bytes decompressed, not the attacker-declared header sizes — a
                // bomb that lies about its entry lengths still trips this once real expansion exceeds the
                // limits the declared-size pre-pass also uses.
                var expansion = CheckExpansionLimits(actualUncompressed, compressedLength, cfg);
                if (expansion is not null)
                    return Result.Fail(expansion);

                await fileStream.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Rejects the bundle when any extracted entry is a symlink whose final target resolves outside the
    /// staging directory. Modern .NET extraction writes link entries as regular files rather than
    /// creating links, so this is defence-in-depth against a filesystem or future extractor that does
    /// materialise them.
    /// </summary>
    private Result ValidateNoEscapingSymlinks(string bundleDir)
    {
        var normalizedBundleDir = PathScope.Normalize(bundleDir);
        foreach (var path in Directory.EnumerateFileSystemEntries(bundleDir, "*", SearchOption.AllDirectories))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null
                && !PathScope.IsSameOrUnderNormalized(PathScope.Normalize(target.FullName), normalizedBundleDir))
            {
                _logger.LogWarning("Bundle contains a symlink at {Path} that escapes the staging root", path);
                return Result.Fail("Bundle contains a symlink that escapes the staging directory.");
            }
        }

        return Result.Success();
    }

    private Result<StagedBundle> ParseStagedBundle(string bundleDir, string bundleId)
    {
        var agentFile = Path.Combine(bundleDir, "AGENT.md");
        if (!File.Exists(agentFile))
            return CleanupAndFail(bundleDir, "Bundle has no AGENT.md at its root.");

        var agent = _agentParser.ParseFromFile(agentFile, bundleDir);
        if (string.IsNullOrEmpty(agent.Id))
            return CleanupAndFail(bundleDir, "Bundle AGENT.md has no resolvable id.");

        var ownedSkills = ParseNestedSkills(bundleDir);
        var manifests = ParsePluginManifests(bundleDir);

        return Result<StagedBundle>.Success(new StagedBundle
        {
            BundleId = bundleId,
            StagedRootDirectory = bundleDir,
            Agent = agent,
            OwnedSkills = ownedSkills,
            PluginManifests = manifests,
        });
    }

    private IReadOnlyList<SkillDefinition> ParseNestedSkills(string bundleDir)
    {
        // Reuses the same nested-skill discovery a host agent uses for its own <agentDir>/skills/, so a
        // malformed SKILL.md is skipped-and-warned rather than aborting the whole bundle. This layer only
        // adds bundle-specific de-duplication (keep first) on top of the shared scan.
        var scanned = NestedSkillScanner.Scan(Path.Combine(bundleDir, "skills"), _skillParser, _logger);

        var byId = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in scanned)
        {
            if (!byId.TryAdd(skill.Id, skill))
                _logger.LogWarning(
                    "Bundle declares duplicate nested skill id '{SkillId}'; keeping the first", skill.Id);
        }

        return [.. byId.Values];
    }

    private IReadOnlyList<PluginManifest> ParsePluginManifests(string bundleDir)
    {
        var manifests = new List<PluginManifest>();

        var rootManifest = _pluginReader.Read(bundleDir);
        if (rootManifest is not null)
            manifests.Add(rootManifest);

        var pluginsRoot = Path.Combine(bundleDir, "plugins");
        if (Directory.Exists(pluginsRoot))
        {
            foreach (var pluginDir in Directory.EnumerateDirectories(pluginsRoot))
            {
                var manifest = _pluginReader.Read(pluginDir);
                if (manifest is not null)
                    manifests.Add(manifest);
            }
        }

        return manifests;
    }

    private Result<StagedBundle> CleanupAndFail(string bundleDir, params string[] errors)
    {
        TryCleanup(bundleDir);
        return Result<StagedBundle>.Fail(errors);
    }

    private void TryCleanup(string bundleDir)
    {
        try
        {
            if (Directory.Exists(bundleDir))
                Directory.Delete(bundleDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up staging directory {BundleDir}", bundleDir);
        }
    }
}
