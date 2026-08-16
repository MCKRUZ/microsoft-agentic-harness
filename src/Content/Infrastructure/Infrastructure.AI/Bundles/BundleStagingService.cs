using System.IO.Compression;
using System.Text.Json;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Bundles;
using Domain.AI.Egress;
using Domain.AI.Skills;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using Domain.Common.Helpers;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Plugins;
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

    /// <summary>
    /// A bundle (unlike a host-installed plugin) has no declaration-level env overrides — this empty,
    /// read-only instance is reused across every server built for every bundle upload rather than
    /// allocating a fresh empty dictionary per server.
    /// </summary>
    private static readonly Dictionary<string, string> NoDeclarationEnv = [];

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly AgentMetadataParser _agentParser;
    private readonly SkillMetadataParser _skillParser;
    private readonly ISkillFileReader _skillFileReader;
    private readonly IPluginManifestReader _pluginReader;
    private readonly IBundleOwnedMcpServerRegistry _bundleOwnedMcpServers;
    private readonly ILogger<BundleStagingService> _logger;

    /// <summary>Initialises the staging service with its parsers, configuration, and logger.</summary>
    /// <param name="appConfig">Monitor over the live application configuration.</param>
    /// <param name="agentParser">Parser that reads a staged bundle's <c>AGENT.md</c>.</param>
    /// <param name="skillParser">Parser that reads each of a staged bundle's <c>SKILL.md</c> files.</param>
    /// <param name="skillFileReader">
    /// Sandboxed, read-only access to skill content. The staging root is one of its permitted roots,
    /// so a staged bundle's own skills remain readable while the scan cannot reach outside it
    /// (issue #247).
    /// </param>
    /// <param name="pluginReader">Reader for a staged bundle's plugin manifest.</param>
    /// <param name="bundleOwnedMcpServers">
    /// The shared, singleton <see cref="IBundleOwnedMcpServerRegistry"/> a bundle's own MCP servers are
    /// merged into under a bundle-scoped namespaced key — deliberately NOT the trusted, host-admin
    /// <c>McpServersConfig</c>; see <see cref="IBundleOwnedMcpServerRegistry"/>'s own doc comment for why.
    /// </param>
    /// <param name="logger">Logger for staging diagnostics.</param>
    public BundleStagingService(
        IOptionsMonitor<AppConfig> appConfig,
        AgentMetadataParser agentParser,
        SkillMetadataParser skillParser,
        ISkillFileReader skillFileReader,
        IPluginManifestReader pluginReader,
        IBundleOwnedMcpServerRegistry bundleOwnedMcpServers,
        ILogger<BundleStagingService> logger)
    {
        ArgumentNullException.ThrowIfNull(skillFileReader);
        ArgumentNullException.ThrowIfNull(bundleOwnedMcpServers);

        _appConfig = appConfig;
        _agentParser = agentParser;
        _skillParser = skillParser;
        _skillFileReader = skillFileReader;
        _pluginReader = pluginReader;
        _bundleOwnedMcpServers = bundleOwnedMcpServers;
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
            // Covers extraction, symlink validation, AND manifest parsing/registration failures — not
            // just extraction, despite the pre-#372 message this replaces. Any MCP server a manifest
            // already registered before the failure has been rolled back by ParsePluginManifests itself
            // by the time this handler runs.
            _logger.LogWarning(ex, "Bundle staging failed while processing {BundleDir}", bundleDir);
            return CleanupAndFail(bundleDir, "Bundle staging failed.");
        }
    }

    private string ResolveStagingRoot(BundleExecutionConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.TempRoot)
            ? SkillContentRoots.BundleStaging(_appConfig.CurrentValue)
            : SkillContentRoots.Resolve(cfg.TempRoot);

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

    private IEnumerable<string> ConfiguredDiscoveryRoots() =>
        SkillContentRoots.Discovery(_appConfig.CurrentValue);

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
        var (manifests, mcpServerNames) = ParsePluginManifests(bundleDir, bundleId);

        return Result<StagedBundle>.Success(new StagedBundle
        {
            BundleId = bundleId,
            StagedRootDirectory = bundleDir,
            Agent = agent,
            OwnedSkills = ownedSkills,
            PluginManifests = manifests,
            McpServerNames = mcpServerNames,
        });
    }

    private IReadOnlyList<SkillDefinition> ParseNestedSkills(string bundleDir)
    {
        // Reuses the same nested-skill discovery a host agent uses for its own <agentDir>/skills/, so a
        // malformed SKILL.md is skipped-and-warned rather than aborting the whole bundle. This layer only
        // adds bundle-specific de-duplication (keep first) on top of the shared scan.
        var scanned = NestedSkillScanner.Scan(
            Path.Combine(bundleDir, "skills"), _skillParser, _skillFileReader, _logger);

        var byId = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in scanned)
        {
            if (!byId.TryAdd(skill.Id, skill))
                _logger.LogWarning(
                    "Bundle declares duplicate nested skill id '{SkillId}'; keeping the first", skill.Id);
        }

        return [.. byId.Values];
    }

    /// <summary>
    /// Parses every plugin manifest a staged bundle declares (its own root <c>plugin.json</c> plus any
    /// nested <c>plugins/*/plugin.json</c>), registering each manifest's MCP servers as it goes.
    /// </summary>
    /// <remarks>
    /// If ANY manifest — or ANY server within a manifest, not just a later manifest — throws while
    /// this runs, every server successfully registered into <see cref="_bundleOwnedMcpServers"/> before
    /// the throw is rolled back (issue #372). <see cref="RegisterBundleMcpServers"/> is handed the SAME
    /// <paramref name="bundleDir"/>-scoped list this method's catch inspects, and appends to it the
    /// instant each server registers — not batched into a separate list and merged in only after that
    /// method returns — so a throw partway through one manifest's OWN multi-server loop still leaves
    /// every already-registered name visible to this catch, not just names from manifests that finished
    /// processing entirely. Without this, staging deletes only the extracted files on failure; the
    /// already-registered servers survive as orphaned, live entries for the rest of the host process's
    /// life, because a bundle that failed to stage is never registered for the eviction path that would
    /// otherwise clean them up.
    /// </remarks>
    private (IReadOnlyList<PluginManifest> Manifests, IReadOnlyList<string> McpServerNames) ParsePluginManifests(
        string bundleDir, string bundleId)
    {
        var manifests = new List<PluginManifest>();
        var mcpServerNames = new List<string>();

        try
        {
            var rootManifest = _pluginReader.Read(bundleDir);
            if (rootManifest is not null)
            {
                manifests.Add(rootManifest);
                RegisterBundleMcpServers(bundleDir, bundleId, rootManifest, mcpServerNames);
            }

            var pluginsRoot = Path.Combine(bundleDir, "plugins");
            if (Directory.Exists(pluginsRoot))
            {
                foreach (var pluginDir in Directory.EnumerateDirectories(pluginsRoot))
                {
                    var manifest = _pluginReader.Read(pluginDir);
                    if (manifest is not null)
                    {
                        manifests.Add(manifest);
                        RegisterBundleMcpServers(pluginDir, bundleId, manifest, mcpServerNames);
                    }
                }
            }

            return (manifests, mcpServerNames);
        }
        catch
        {
            foreach (var registered in mcpServerNames)
                _bundleOwnedMcpServers.TryRemove(registered);

            throw;
        }
    }

    /// <summary>
    /// Merges one manifest's declared MCP servers into the shared <see cref="BundleOwnedMcpServerRegistry"/>
    /// — deliberately NOT the trusted, host-admin <c>McpServersConfig</c> — under a bundle-scoped key
    /// (<c>{bundleId}:{serverName}</c>), appending each registered key directly into
    /// <paramref name="mcpServerNames"/> (shared with <see cref="ParsePluginManifests"/>'s rollback catch,
    /// and eventually <see cref="StagedBundle.McpServerNames"/>) AS EACH SERVER REGISTERS — not into a
    /// local list returned only once this whole method completes, so a throw anywhere in this method's own
    /// loop still leaves every server it already registered visible to the caller's rollback (issue #372).
    /// <paramref name="bundleId"/> alone (not the manifest's own name) is the namespace, because
    /// <see cref="Domain.AI.Bundles.StagedBundle.BundleId"/> is a fresh GUID per staging — two bundles can
    /// never collide — while a manifest's <c>Name</c> is free text and not guaranteed unique. A malformed
    /// or missing <c>mcp.json</c> is skipped and logged, never fails staging.
    /// </summary>
    private void RegisterBundleMcpServers(
        string manifestBaseDir, string bundleId, PluginManifest manifest, List<string> mcpServerNames)
    {
        if (string.IsNullOrEmpty(manifest.McpServers))
            return;

        // A bundle is untrusted, externally-authored input. Honouring its own declared MCP servers means
        // the host makes outbound connections a caller's CapabilityEnvelope never authorized — off by
        // default until an operator opts in. The manifest is never even opened when off, so a disabled
        // host pays no parsing cost for a capability it doesn't have.
        if (!_appConfig.CurrentValue.AI.BundleExecution.AllowBundleDeclaredMcpServers)
        {
            _logger.LogInformation(
                "Bundle {BundleId}: declares MCP servers but AllowBundleDeclaredMcpServers is disabled — " +
                "skipping, none registered. Set AppConfig:AI:BundleExecution:AllowBundleDeclaredMcpServers " +
                "= true to enable this capability.",
                bundleId);
            return;
        }

        using var block = Infrastructure.AI.Plugins.McpManifestReader.ReadMcpServersBlock(
            manifestBaseDir, manifest.McpServers, $"Bundle {bundleId}", _logger);
        if (block is null)
            return;

        // Loop-invariant: every server this manifest declares is checked against the SAME harness-wide
        // allowlist, so it is mapped from config once here rather than re-derived on every iteration.
        var allowlist = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);

        foreach (var serverProp in block.Value.ServersElement.EnumerateObject())
        {
            var namespacedName = $"{bundleId}:{serverProp.Name}";
            if (TryBuildAndRegisterOneServer(bundleId, namespacedName, serverProp, allowlist))
                mcpServerNames.Add(namespacedName);
        }
    }

    /// <summary>
    /// Builds one manifest-declared server and registers it under <paramref name="namespacedName"/>,
    /// rejecting a stdio (local-command) transport, a disallowed destination, and a duplicate name — see
    /// <see cref="RegisterBundleMcpServers"/>. Returns whether registration succeeded.
    /// </summary>
    private bool TryBuildAndRegisterOneServer(
        string bundleId, string namespacedName, JsonProperty serverProp, IReadOnlyList<EgressAllowlistEntry> allowlist)
    {
        var buildResult = McpServerDefinitionBuilder.Build(
            // A bundle (unlike a host-installed plugin) has no declaration-level env overrides.
            serverProp.Value, NoDeclarationEnv, $"[Bundle: {bundleId}]", serverProp.Name);
        if (!buildResult.IsSuccess)
        {
            _logger.LogWarning(
                "Bundle {BundleId}: failed to build MCP server definition for '{ServerName}', skipping: {Errors}",
                bundleId, serverProp.Name, string.Join("; ", buildResult.Errors));
            return false;
        }

        var definition = buildResult.Value!;

        if (!definition.IsRemoteServer)
        {
            LogStdioRejected(bundleId, serverProp.Name);
            return false;
        }

        if (!IsUrlAllowlisted(bundleId, serverProp.Name, definition.Url, allowlist))
            return false;

        if (_bundleOwnedMcpServers.TryAdd(namespacedName, definition))
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: duplicate MCP server name '{ServerName}' declared across " +
            "more than one plugin manifest; keeping the first",
            bundleId, serverProp.Name);
        return false;
    }

    // A bundle is untrusted, uploader-supplied content. A stdio server's Command/Args/Env come
    // straight from the bundle's own manifest, and connecting to it launches that command as a
    // real host process (via McpConnectionManager -> StdioClientTransport) — arbitrary command
    // execution on the harness host, gated only by upload permission. Host-installed plugins
    // (PluginLoader) are a different trust tier and are unaffected: only this bundle path rejects
    // Stdio. Tracked follow-up to run a bundle's stdio server inside the existing process/Docker
    // sandbox instead of rejecting it outright: #371.
    private void LogStdioRejected(string bundleId, string serverName)
    {
        // "resolved to", not "declares": McpServerDefinitionBuilder.ParseType defaults an ABSENT or
        // UNRECOGNIZED 'type' value to Stdio too (only "http"/"sse" are matched), so a bundle author
        // who misspells a real remote transport lands here as well — this message must stay accurate
        // for both cases, not assert an explicit stdio declaration that may not exist.
        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' resolved to a stdio (local-command) " +
            "transport, which is not permitted for bundle-owned servers — rejected, not registered. " +
            "The transport was either explicitly declared, or defaulted to stdio because 'type' was " +
            "missing or unrecognized (only 'http'/'sse' are supported).",
            bundleId, serverName);
    }

    /// <summary>
    /// Registration-time pre-check against the SAME harness-wide allowlist a bundle-owned server's live
    /// connections are evaluated against (<c>McpConnectionManager</c>'s per-request egress policy —
    /// Infrastructure.AI.MCP, a different project this one does not reference), sharing
    /// <see cref="DefaultEgressPolicy.MatchesAnyEntry"/>'s matching loop directly with the live decision
    /// (<see cref="DefaultEgressPolicy.AllowAsync"/>) so the two matching primitives can never
    /// independently drift on what "host/scheme/port matches THIS list" means. This is deliberately NOT a
    /// claim that this check and the live decision always agree: the live path resolves its policy
    /// per-identity via <c>IEgressPolicyResolver</c>, which can source a different, wider, or differently
    /// cached allowlist than the one read fresh from config here — the two can diverge on WHICH list
    /// applies, even though they never diverge on what matching against a given list means. This is a
    /// fast, synchronous, no-DNS gate at upload time so an obviously-disallowed (or unparsable)
    /// destination is rejected before a bundle handle ever exists — not a replacement for the audited,
    /// per-request enforcement every actual connection still goes through, and not a guarantee that
    /// passing this gate implies the live connection will also be allowed.
    /// </summary>
    private bool IsUrlAllowlisted(
        string bundleId, string serverName, string? url, IReadOnlyList<EgressAllowlistEntry> allowlist)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning(
                "Bundle {BundleId}: MCP server '{ServerName}' declares an unparsable or missing URL " +
                "'{Url}' — rejected, not registered.",
                bundleId, serverName, url);
            return false;
        }

        if (DefaultEgressPolicy.MatchesAnyEntry(allowlist, uri))
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' declares URL '{Url}', whose host is not on " +
            "AppConfig:AI:Egress:DefaultAllowlist — rejected, not registered.",
            bundleId, serverName, url);
        return false;
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
