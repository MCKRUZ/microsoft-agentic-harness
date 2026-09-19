using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Discovers and caches skill metadata by scanning filesystem directories for SKILL.md files.
/// </summary>
/// <remarks>
/// <para>
/// Walks the filesystem itself (up to <see cref="MaxSearchDepth"/> levels) rather than using the
/// framework's <c>AgentFileSkillsSource</c>, which drops the <c>category</c>, <c>tags</c>, and
/// <c>skill_type</c> keys this registry indexes on — see <see cref="SkillMetadataParser"/>.
/// </para>
/// <para>
/// Runtime skill content disclosure is a separate concern, handled by the framework's
/// <see cref="Microsoft.Agents.AI.AgentSkillsProvider"/> wired into
/// <c>ChatClientAgentOptions.AIContextProviders</c>.
/// </para>
/// <para>
/// Also implements <see cref="ISkillRegistryRefresher"/> (issue #709, mirroring
/// <c>Agents.AgentMetadataRegistry</c> from issue #705): the cache can be invalidated or eagerly
/// rebuilt after the initial load, by <c>SkillManifestWatcherService</c> reacting to filesystem
/// changes or by an operator-triggered refresh command.
/// </para>
/// <para>
/// <b><see cref="Version"/> exists for consumers with their own derived cache (security-review
/// finding on #709).</b> Two production components previously assumed this registry's data never
/// changed after startup: <c>SkillManifestEgressPolicyResolver</c> cached a per-skill egress policy
/// forever, so narrowing or revoking a skill's allowed outbound hosts would keep being enforced as
/// the old, broader policy until process restart — actively dangerous once this class made
/// hot-reload the advertised way to apply that kind of change. <c>PluginPermissionRuleProvider</c>
/// cached its plugin-tool rules keyed only on the plugin registry's own version, with its own doc
/// comment explicitly relying on this registry being "one-shot with no invalidation" — a statement
/// this class now makes false. Both now key their cache on <see cref="Version"/> too.
/// </para>
/// </remarks>
public sealed class SkillMetadataRegistry : ISkillMetadataRegistry, ISkillRegistryRefresher
{
    private const int MaxSearchDepth = 3;

    private readonly ILogger<SkillMetadataRegistry> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly SkillMetadataParser _parser;
    private readonly ISkillFileReader _fileReader;
    private readonly IPluginRegistry? _pluginRegistry;

    private Dictionary<string, SkillDefinition>? _cache;
    private volatile bool _stale;
    private IReadOnlyList<string> _searchedPaths = [];
    private long _version;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillMetadataRegistry"/> class.
    /// </summary>
    /// <param name="logger">Logger for discovery diagnostics.</param>
    /// <param name="appConfig">Monitor over the live application configuration (skill search paths).</param>
    /// <param name="parser">Parser that reads a SKILL.md file into a <see cref="SkillDefinition"/>.</param>
    /// <param name="fileReader">
    /// Sandboxed, read-only access to skill content. The discovery walk probes and enumerates
    /// through it so it cannot be led outside the configured skill roots (issue #247).
    /// </param>
    /// <param name="pluginRegistry">
    /// Optional registry of loaded plugins. When supplied, each discovered skill whose directory
    /// falls under a loaded plugin's <c>SkillPaths</c> is attributed to that plugin via
    /// <see cref="SkillDefinition.PluginSource"/>, which activates plugin boundary governance
    /// (AllowedTools/DeniedTools and Injected tool-resolution mode). Null in hosts that do not load
    /// plugins (for example the standalone MCP server), where all skills are treated as built-in.
    /// </param>
    public SkillMetadataRegistry(
        ILogger<SkillMetadataRegistry> logger,
        IOptionsMonitor<AppConfig> appConfig,
        SkillMetadataParser parser,
        ISkillFileReader fileReader,
        IPluginRegistry? pluginRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(fileReader);

        _logger = logger;
        _appConfig = appConfig;
        _parser = parser;
        _fileReader = fileReader;
        _pluginRegistry = pluginRegistry;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _searchedPaths;

    /// <inheritdoc />
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetAll() => [.. GetOrLoadCache().Values];

    /// <inheritdoc />
    public SkillDefinition? TryGet(string skillId) =>
        GetOrLoadCache().TryGetValue(skillId, out var skill) ? skill : null;

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByCategory(string category) =>
        GetOrLoadCache().Values
            .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByTags(IEnumerable<string> tags)
    {
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return GetOrLoadCache().Values
            .Where(s => s.Tags.Any(t => tagSet.Contains(t)))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetBySkillType(string skillType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillType);
        return GetOrLoadCache().Values
            .Where(s => string.Equals(s.SkillType, skillType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Marks the cache stale WITHOUT discarding it (issue #709, mirroring
    /// <c>Agents.AgentMetadataRegistry.Invalidate</c> from issue #705 — see that method's remarks
    /// for the full history of why nulling the cache here is wrong). The next call to any read
    /// method triggers a lazy rebuild via <see cref="GetOrLoadCache"/>, which — like an explicit
    /// <see cref="Refresh"/> — reconciles against the last known snapshot rather than an empty one.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _stale = true;

            // Advances Version here too, not only inside RebuildAndReconcile (CI correctness-review
            // finding on #709's own security fix): a consumer like SkillManifestEgressPolicyResolver
            // checks Version on every call but only calls back into THIS registry (TryGet/GetAll/…)
            // on a cache MISS — the whole point of caching. Before this fix, an automatic
            // watcher-driven Invalidate() left Version unchanged, so such a consumer's cache stayed a
            // hit forever and the registry itself was never asked to rebuild — Version was stuck,
            // waiting for a read that caching was specifically designed to avoid. Bumping it here
            // means "the data may be stale" propagates to a Version-checking consumer immediately,
            // not only after some unrelated caller happens to read this registry first.
            Interlocked.Increment(ref _version);
        }
    }

    /// <inheritdoc />
    public SkillRegistryRefreshResult Refresh()
    {
        lock (_lock)
        {
            return RebuildAndReconcile();
        }
    }

    /// <summary>
    /// Returns the current cache, rebuilding it if it has never been loaded or has been marked stale
    /// by <see cref="Invalidate"/>. A single local snapshot is captured and returned — every caller
    /// reads through that one reference rather than touching the <c>_cache</c> field a second time,
    /// so a concurrent invalidation landing between two field reads can never null-reference a
    /// reader. The returned dictionary is never mutated after <see cref="Discover"/> builds it — only
    /// replaced wholesale — so a reader holding an older snapshot during a concurrent reload is safe.
    /// </summary>
    private Dictionary<string, SkillDefinition> GetOrLoadCache()
    {
        var cache = _cache;
        if (cache is not null && !_stale)
            return cache;

        lock (_lock)
        {
            cache = _cache;
            if (cache is not null && !_stale)
                return cache;

            try
            {
                RebuildAndReconcile();
            }
            catch (Exception ex) when (ex is not SkillPathRefusedException && _cache is not null)
            {
                // A rebuild failure must not strand a previously-successful load (mirrors the fix
                // already shipped for AgentMetadataRegistry on #705): once _cache was populated it
                // must not become permanently unreachable just because ONE later rebuild attempt
                // failed. Serve the last known set instead; _stale stays true so the next
                // Invalidate/Refresh still retries.
                //
                // SkillPathRefusedException is deliberately EXCLUDED from this tolerance — a
                // departure from copying AgentMetadataRegistry's catch verbatim, since agents have
                // no equivalent exception type. A sandbox refusal is a security-relevant
                // misconfiguration (a configured root resolved outside the allowed skill content
                // roots): every other catch in this class already lets it propagate rather than
                // silently tolerating it, and "quietly keep serving stale data" would be the wrong
                // failure mode for a boundary violation — it should be loud, not absorbed.
                _logger.LogError(ex,
                    "Skill registry rebuild failed — continuing to serve the last known skill set");
                return _cache;
            }

            return _cache!;
        }
    }

    /// <summary>
    /// Rediscovers skills from disk, diffs the result against the last known snapshot, and swaps in
    /// the new cache — the single reconciling rebuild both <see cref="Refresh"/> and the lazy path in
    /// <see cref="GetOrLoadCache"/> share (issue #709). Caller must hold <see cref="_lock"/>.
    /// </summary>
    private SkillRegistryRefreshResult RebuildAndReconcile()
    {
        var previous = _cache ?? new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var previousSearchedPaths = _searchedPaths;
        var (next, hadEnumerationErrors) = Discover();

        var added = new List<string>();
        var updated = new List<string>();
        foreach (var (id, skill) in next)
        {
            if (!previous.TryGetValue(id, out var previousSkill))
                added.Add(id);
            else if (!DefinitionsAreEquivalent(previousSkill, skill))
                updated.Add(id);
        }

        var removed = previous.Keys.Where(id => !next.ContainsKey(id)).ToList();

        // Two independent ways this scan can be too unreliable to trust for deciding what's GONE
        // (mirrors AgentMetadataRegistry.RebuildAndReconcile from #705 — see that method's remarks
        // for the full reasoning):
        //
        // (1) A directory this pass failed to ENUMERATE (permission hiccup, network share stutter, a
        //     mid-rename) — hadEnumerationErrors.
        // (2) A root that resolved and was searched LAST time no longer resolves at all this time —
        //     SkillSearchPathResolver.Resolve treats "not found" as an ORDINARY, expected condition,
        //     since a template consumer's unused AdditionalPaths entry must not break discovery of
        //     everything else, so it never sets hadEnumerationErrors on its own.
        //
        // Either way, a skill missing only because its folder (or the root above it) briefly failed
        // to resolve looks identical to one that was really deleted. Carry the previous definition
        // forward into `next` so the skill stays exactly as it was until a clean, error-free scan
        // actually confirms one way or the other — do not report it as removed, do not lose it.
        var rootsVanished = previousSearchedPaths.Any(
            p => !_searchedPaths.Contains(p, StringComparer.OrdinalIgnoreCase));
        var scanIsUnreliable = hadEnumerationErrors || rootsVanished;

        if (scanIsUnreliable && removed.Count > 0)
        {
            _logger.LogWarning(
                "Skill registry rebuild scan was unreliable ({Reason}) and would have classified " +
                "{RemovedCount} skill(s) as removed — keeping them as-is this cycle since the scan " +
                "may be incomplete rather than those skills actually being gone",
                hadEnumerationErrors && rootsVanished ? "enumeration errors and a vanished root"
                    : hadEnumerationErrors ? "enumeration errors" : "a vanished root",
                removed.Count);

            foreach (var id in removed)
                next[id] = previous[id];

            removed = [];
        }

        _cache = next;
        _stale = false;
        Interlocked.Increment(ref _version);

        _logger.LogInformation(
            "Skill registry rebuilt: {Added} added, {Updated} updated, {Removed} removed, {Total} total",
            added.Count, updated.Count, removed.Count, next.Count);

        return new SkillRegistryRefreshResult
        {
            Added = added,
            Updated = updated,
            Removed = removed,
            TotalSkillCount = next.Count,
            SearchedPaths = _searchedPaths
        };
    }

    /// <summary>
    /// Compares two definitions for the same skill id for the purpose of the
    /// <see cref="SkillRegistryRefreshResult.Updated"/> classification. Deliberately not full-object
    /// equality: <see cref="SkillDefinition.LoadedAt"/> is stamped fresh on every parse, so a
    /// byte-for-byte-unchanged manifest would otherwise compare unequal on every single reload.
    /// Compares the identity and behaviour-affecting fields a change to SKILL.md would actually touch
    /// — not the derived resource lists (Templates/References/Scripts/Assets), whose deep comparison
    /// would be expensive and whose presence is already implied by a change to the fields compared.
    /// </summary>
    /// <remarks>
    /// Includes <see cref="SkillDefinition.Egress"/>'s allowlist (security-review finding on #709): an
    /// edit that narrows or revokes a skill's egress allowlist is exactly the case where "reported as
    /// unchanged" would be actively misleading to an operator relying on the refresh summary to
    /// confirm their edit took effect — <see cref="ISkillMetadataRegistry.Version"/> is what actually
    /// keeps <c>SkillManifestEgressPolicyResolver</c> safe regardless of this comparison's accuracy,
    /// but the reported summary should still be honest about what changed.
    /// </remarks>
    private static bool DefinitionsAreEquivalent(SkillDefinition a, SkillDefinition b) =>
        a.Id == b.Id
        && a.Name == b.Name
        && a.Description == b.Description
        && a.Instructions == b.Instructions
        && a.Objectives == b.Objectives
        && a.TraceFormat == b.TraceFormat
        && a.Version == b.Version
        && a.Author == b.Author
        && a.PluginSource == b.PluginSource
        && a.Category == b.Category
        && a.SkillType == b.SkillType
        && a.ModelOverride == b.ModelOverride
        && a.CompletionTool == b.CompletionTool
        && a.FilePath == b.FilePath
        && a.BaseDirectory == b.BaseDirectory
        && a.Tags.SequenceEqual(b.Tags)
        && (a.AllowedTools ?? []).SequenceEqual(b.AllowedTools ?? [])
        && a.Prerequisites.SequenceEqual(b.Prerequisites)
        && (a.Egress?.Allowlist ?? []).SequenceEqual(b.Egress?.Allowlist ?? []);

    /// <summary>
    /// Scans every configured, existing skill path and returns the discovered skills alongside
    /// whether any directory along the way failed to enumerate. That second flag exists solely so
    /// <see cref="RebuildAndReconcile"/> can tell "this skill is genuinely gone" apart from "we
    /// failed to see it this pass" (issue #709) — a distinction the result dictionary alone cannot
    /// make.
    /// </summary>
    private (Dictionary<string, SkillDefinition> Skills, bool HadEnumerationErrors) Discover()
    {
        GuardPluginRegistryPresent();

        var skillsConfig = _appConfig.CurrentValue.AI?.Skills;
        var resolvedPaths = SkillSearchPathResolver.Resolve(skillsConfig, _fileReader, _logger);
        _searchedPaths = resolvedPaths;

        var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var hadEnumerationErrors = false;

        if (resolvedPaths.Count > 0)
        {
            var pluginPaths = ResolvePluginSkillPaths();

            foreach (var rootPath in resolvedPaths)
                DiscoverInDirectory(rootPath, depth: 0, pluginPaths, result, ref hadEnumerationErrors);
        }

        _logger.LogInformation(
            "Skill discovery complete: {Count} skills found across {PathCount} path(s)",
            result.Count, resolvedPaths.Count);

        return (result, hadEnumerationErrors);
    }

    /// <summary>
    /// Fails fast when the host declares plugins but no <see cref="IPluginRegistry"/> was registered.
    /// Without the registry, discovered skills can never be attributed to their owning plugin, so
    /// plugin boundary governance (AllowedTools/DeniedTools and AutonomyLevel) would silently no-op —
    /// a security-relevant misconfiguration. A clear exception is preferable to ungoverned plugin
    /// tools. Hosts that declare no plugins (for example the standalone MCP server) are unaffected:
    /// the registry stays legitimately optional there.
    /// </summary>
    /// <remarks>
    /// Runs on every <see cref="Discover"/> pass, not just the first (issue #709): a reload must
    /// re-check this the same way it re-checks everything else. In practice the declared-plugins
    /// configuration is static for the process lifetime, so a passing check stays passing — but
    /// should this guard ever fail on a later pass, <see cref="GetOrLoadCache"/>'s failure-tolerance
    /// catch serves the last known good set rather than stranding the registry.
    /// </remarks>
    private void GuardPluginRegistryPresent()
    {
        var declaredPlugins = _appConfig.CurrentValue.AI?.Plugins?.Packages?.Count ?? 0;
        if (declaredPlugins > 0 && _pluginRegistry is null)
        {
            throw new InvalidOperationException(
                $"{declaredPlugins} plugin(s) are declared under AppConfig.AI.Plugins.Packages, but no " +
                $"{nameof(IPluginRegistry)} is registered. Plugin boundary governance " +
                "(AllowedTools/DeniedTools/AutonomyLevel) cannot be enforced without it. Register the " +
                "plugin services (which include IPluginRegistry) or remove the plugin declarations.");
        }
    }

    /// <summary>
    /// Snapshots the skill directories of every successfully-loaded plugin, paired with the
    /// plugin name. Used to attribute each discovered skill to its owning plugin so boundary
    /// governance can apply. Empty when no plugin registry is available or no plugins are loaded.
    /// Re-read on every <see cref="Discover"/> pass, so a plugin loaded or unloaded after startup is
    /// correctly reflected on the next reload.
    /// </summary>
    private IReadOnlyList<(string Path, string PluginName)> ResolvePluginSkillPaths()
    {
        if (_pluginRegistry is null)
            return [];

        var pairs = new List<(string Path, string PluginName)>();
        foreach (var plugin in _pluginRegistry.GetLoadedPlugins())
        {
            if (plugin.Status != PluginLoadStatus.Loaded)
                continue;

            foreach (var skillPath in plugin.SkillPaths)
            {
                if (string.IsNullOrWhiteSpace(skillPath))
                    continue;
                pairs.Add((PathScope.Normalize(skillPath), plugin.Name));
            }
        }

        return pairs;
    }

    private void DiscoverInDirectory(
        string directory,
        int depth,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths,
        Dictionary<string, SkillDefinition> result,
        ref bool hadEnumerationErrors)
    {
        if (depth > MaxSearchDepth)
            return;

        var skillFile = Path.Combine(directory, "SKILL.md");

        if (_fileReader.FileExists(skillFile))
        {
            TryAddSkill(skillFile, directory, pluginPaths, result);

            // A directory with SKILL.md is a skill — don't recurse into it
            return;
        }

        // Recurse into subdirectories to find nested skills. The reader drops any subdirectory the
        // sandbox refuses (a junction out of the skill roots, say), so the walk cannot be steered
        // outside the roots by planting one.
        try
        {
            foreach (var subDir in _fileReader.EnumerateDirectories(directory))
                DiscoverInDirectory(subDir, depth + 1, pluginPaths, result, ref hadEnumerationErrors);
        }
        catch (Exception ex) when (ex is not SkillPathRefusedException)
        {
            // A sandbox refusal propagates. Swallowing it would report "no skills here", which is
            // indistinguishable from an empty directory, so a misconfigured root would boot an
            // agent with none of its skills rather than failing. Tolerating an unreadable
            // directory is the point of this catch; tolerating being told it is out of bounds is not.
            _logger.LogWarning(ex, "Could not enumerate directory: {Path}", directory);
            hadEnumerationErrors = true;
        }
    }

    private void TryAddSkill(
        string skillFile,
        string directory,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths,
        Dictionary<string, SkillDefinition> result)
    {
        try
        {
            var pluginSource = ResolveOwningPlugin(directory, pluginPaths);
            var definition = _parser.ParseFromFile(skillFile, directory, pluginSource);
            if (string.IsNullOrEmpty(definition.Id))
                return;

            // Config/built-in paths are walked before plugin paths (see SkillsConfig.AllPaths),
            // so the first definition for an ID wins. This prevents a plugin from shadowing a
            // built-in skill's system prompt via an ID collision.
            if (result.TryGetValue(definition.Id, out var existing))
            {
                _logger.LogWarning(
                    "Skill ID collision on '{SkillId}': keeping first from {ExistingPath} (source: {ExistingSource}); " +
                    "ignoring duplicate from {DuplicatePath} (source: {DuplicateSource})",
                    definition.Id,
                    existing.BaseDirectory, existing.PluginSource ?? "built-in",
                    directory, pluginSource ?? "built-in");
                return;
            }

            result[definition.Id] = definition;
            _logger.LogDebug(
                "Discovered skill: {SkillId} from {Path} (source: {Source})",
                definition.Id, directory, pluginSource ?? "built-in");
        }
        catch (Exception ex) when (ex is not SkillPathRefusedException)
        {
            // Same rule as the enumeration above: a malformed manifest is skipped, a refused one is
            // not — dropping it would leave the skill absent with only a warning to say why.
            _logger.LogWarning(ex, "Failed to parse skill from {Path}", skillFile);
        }
    }

    /// <summary>
    /// Returns the name of the loaded plugin that owns <paramref name="skillDirectory"/>, or null
    /// when the skill is built-in. A plugin owns the directory when the directory equals, or is
    /// nested under, one of the plugin's skill paths. The most specific (longest) matching path
    /// wins so nested plugin layouts attribute correctly.
    /// </summary>
    private static string? ResolveOwningPlugin(
        string skillDirectory,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths)
    {
        if (pluginPaths.Count == 0)
            return null;

        var normalizedDir = PathScope.Normalize(skillDirectory);
        string? bestName = null;
        var bestLength = -1;

        foreach (var (path, pluginName) in pluginPaths)
        {
            if (PathScope.IsSameOrUnderNormalized(normalizedDir, path) && path.Length > bestLength)
            {
                bestName = pluginName;
                bestLength = path.Length;
            }
        }

        return bestName;
    }
}
