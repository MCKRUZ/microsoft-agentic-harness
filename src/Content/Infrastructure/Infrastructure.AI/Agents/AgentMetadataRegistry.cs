using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Agents;
using Domain.Common.Config;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Discovers and caches <see cref="AgentDefinition"/>s by scanning filesystem directories for
/// <c>AGENT.md</c> files. Search paths are taken from <c>AppConfig.AI.Agents</c> and walked up to
/// <see cref="MaxSearchDepth"/> levels deep; a directory containing an <c>AGENT.md</c> is treated
/// as an agent root and is not recursed into further.
/// </summary>
/// <remarks>
/// Mirrors the behaviour of <c>SkillMetadataRegistry</c> so agent and skill discovery share an
/// identical operational model: lazy first-load, dictionary-backed cache keyed by id, and
/// best-effort parsing that logs but does not fail the host when a manifest is malformed.
/// Also implements <see cref="IAgentRegistryRefresher"/> (issue #705): the cache can be invalidated
/// or eagerly rebuilt after the initial load, by <c>AgentManifestWatcherService</c>
/// reacting to filesystem changes or by an operator-triggered refresh command.
/// </remarks>
public sealed class AgentMetadataRegistry : IAgentMetadataRegistry, IAgentRegistryRefresher
{
    private const int MaxSearchDepth = 3;

    private readonly ILogger<AgentMetadataRegistry> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly AgentMetadataParser _parser;
    private readonly SkillMetadataParser _skillParser;
    private readonly ISkillFileReader _skillFileReader;
    private readonly AgentOwnedSkillStore _ownedSkills;

    private Dictionary<string, AgentDefinition>? _cache;
    private bool _stale;
    private IReadOnlyList<string> _searchedPaths = [];
    private readonly Lock _lock = new();

    /// <summary>Initialises the registry with its dependencies.</summary>
    /// <param name="logger">Logger for discovery diagnostics.</param>
    /// <param name="appConfig">Monitor over the live application configuration (agent search paths).</param>
    /// <param name="parser">Parser that reads an <c>AGENT.md</c> file into an <see cref="AgentDefinition"/>.</param>
    /// <param name="skillParser">Parser used to read each agent's own nested <c>SKILL.md</c> files.</param>
    /// <param name="skillFileReader">
    /// Sandboxed, read-only access to skill content, confining the nested-skill scan to the
    /// configured skill and agent roots (issue #247).
    /// </param>
    /// <param name="ownedSkills">
    /// Store populated during discovery with the skills found under each agent's
    /// <c>&lt;agentDir&gt;/skills/</c> directory, so <c>AgentFactory</c> can resolve them ahead of the
    /// global registry without polluting it. Taken as the concrete type — not
    /// <see cref="IAgentOwnedSkillStore"/> — because discovery needs <c>ReplaceAgentSkills</c> and
    /// <c>RemoveAgent</c> for reload reconciliation (issue #705), which are deliberately not on the
    /// interface the per-bundle-run overlay decorator also implements.
    /// </param>
    public AgentMetadataRegistry(
        ILogger<AgentMetadataRegistry> logger,
        IOptionsMonitor<AppConfig> appConfig,
        AgentMetadataParser parser,
        SkillMetadataParser skillParser,
        ISkillFileReader skillFileReader,
        AgentOwnedSkillStore ownedSkills)
    {
        ArgumentNullException.ThrowIfNull(skillFileReader);

        _logger = logger;
        _appConfig = appConfig;
        _parser = parser;
        _skillParser = skillParser;
        _skillFileReader = skillFileReader;
        _ownedSkills = ownedSkills;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _searchedPaths;

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetAll() => [.. GetOrLoadCache().Values];

    /// <inheritdoc />
    public AgentDefinition? TryGet(string agentId) =>
        GetOrLoadCache().TryGetValue(agentId, out var agent) ? agent : null;

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByCategory(string category) =>
        GetOrLoadCache().Values
            .Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByTags(IEnumerable<string> tags)
    {
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return GetOrLoadCache().Values
            .Where(a => a.Tags.Any(t => tagSet.Contains(t)))
            .ToList();
    }

    /// <summary>
    /// Marks the cache stale WITHOUT discarding it (issue #705 follow-up — see remarks). The next call
    /// to any read method triggers a lazy rebuild via <see cref="GetOrLoadCache"/>, which — like an
    /// explicit <see cref="Refresh"/> — reconciles <see cref="AgentOwnedSkillStore"/> against whatever
    /// changed.
    /// </summary>
    /// <remarks>
    /// The original implementation set <c>_cache = null</c> here, which broke two things once a
    /// filesystem watcher started calling this automatically: (1) it discarded the only record of what
    /// was previously loaded, so a <see cref="Refresh"/> that ran afterward diffed against an empty set
    /// instead of the real prior state — every surviving agent misreported as "added," every removal
    /// silently missed; (2) the automatic (watcher-driven) path never calls <see cref="Refresh"/> at
    /// all — it calls only this method — and the pre-fix <c>Discover</c> performed no reconciliation of
    /// its own, so a deleted agent's owned skills were never cleaned up on the default, no-restart path
    /// this issue exists to support; only an operator explicitly hitting the refresh endpoint reconciled
    /// them. Both are symptoms of the same mistake: reconciliation lived only in <see cref="Refresh"/>,
    /// while invalidation destroyed the baseline that reconciliation needs. Fixed by keeping the last
    /// snapshot in place — <see cref="GetOrLoadCache"/> now rebuilds through the same reconciling path
    /// <see cref="Refresh"/> uses whenever <see cref="_stale"/> is set, so cleanup happens on every
    /// reload, automatic or operator-triggered, and always against an accurate prior snapshot.
    /// </remarks>
    public void Invalidate()
    {
        lock (_lock)
        {
            _stale = true;
        }
    }

    /// <inheritdoc />
    public AgentRegistryRefreshResult Refresh()
    {
        lock (_lock)
        {
            return RebuildAndReconcile();
        }
    }

    /// <summary>
    /// Returns the current cache, rebuilding it if it has never been loaded or has been marked stale by
    /// <see cref="Invalidate"/>. A single local snapshot is captured and returned — every caller reads
    /// through that one reference rather than touching the <c>_cache</c> field a second time, so a
    /// concurrent invalidation landing between two field reads can never null-reference a reader. The
    /// returned dictionary is never mutated after <see cref="Discover"/> builds it — only replaced
    /// wholesale — so a reader holding an older snapshot during a concurrent reload is safe.
    /// </summary>
    private Dictionary<string, AgentDefinition> GetOrLoadCache()
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
            catch (Exception ex) when (_cache is not null)
            {
                // A rebuild failure must not strand a previously-successful load (code review on
                // issue #705): before Invalidate() existed, once _cache was populated it was never
                // touched again, so a bad configured path could never break an already-working
                // registry. Now, ANY exception here would otherwise leave _stale permanently true —
                // every subsequent call re-enters this branch, retries, fails, and re-throws, forever
                // discarding perfectly good data still sitting in _cache. Serve the last known set
                // instead; _stale stays true so the next Invalidate/Refresh still retries.
                _logger.LogError(ex,
                    "Agent registry rebuild failed — continuing to serve the last known agent set");
                return _cache;
            }

            return _cache!;
        }
    }

    /// <summary>
    /// Rediscovers agents from disk, diffs the result against the last known snapshot, reconciles
    /// <see cref="AgentOwnedSkillStore"/> for every agent id that disappeared, and swaps in the new
    /// cache — the single reconciling rebuild both <see cref="Refresh"/> and the lazy path in
    /// <see cref="GetOrLoadCache"/> share, so cleanup is never skipped regardless of which one triggered
    /// it (issue #705). Caller must hold <see cref="_lock"/>.
    /// </summary>
    private AgentRegistryRefreshResult RebuildAndReconcile()
    {
        var previous = _cache ?? new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        var (next, hadEnumerationErrors) = Discover();

        var added = new List<string>();
        var updated = new List<string>();
        foreach (var (id, agent) in next)
        {
            if (!previous.TryGetValue(id, out var previousAgent))
                added.Add(id);
            else if (!DefinitionsAreEquivalent(previousAgent, agent))
                updated.Add(id);
        }

        var removed = previous.Keys.Where(id => !next.ContainsKey(id)).ToList();

        // A directory this pass failed to enumerate (permission hiccup, network share stutter, a
        // mid-rename) makes `next` unreliable for the purpose of deciding what's GONE — an agent
        // missing only because its folder briefly failed to read looks identical to one that was
        // really deleted. The registry cache itself self-heals on the next successful scan either
        // way (SyncAgentOwnedSkills re-populates every currently-found agent's skills unconditionally
        // — see its own remarks), but wiping AgentOwnedSkillStore here is NOT self-healing: nothing
        // repopulates a still-present agent's private skills until IT is rediscovered too. So skip
        // only that one destructive step on a partial scan, and say so (code review on issue #705).
        if (hadEnumerationErrors && removed.Count > 0)
        {
            _logger.LogWarning(
                "Agent registry rebuild hit directory enumeration errors and would have classified " +
                "{RemovedCount} agent(s) as removed — skipping owned-skill cleanup for them this cycle " +
                "since the scan may be incomplete rather than those agents actually being gone",
                removed.Count);
        }
        else
        {
            foreach (var id in removed)
                _ownedSkills.RemoveAgent(id);
        }

        _cache = next;
        _stale = false;

        _logger.LogInformation(
            "Agent registry rebuilt: {Added} added, {Updated} updated, {Removed} removed, {Total} total",
            added.Count, updated.Count, removed.Count, next.Count);

        return new AgentRegistryRefreshResult
        {
            Added = added,
            Updated = updated,
            Removed = removed,
            TotalAgentCount = next.Count,
            SearchedPaths = _searchedPaths
        };
    }

    /// <summary>
    /// Compares two definitions for the same agent id for the purpose of the
    /// <see cref="AgentRegistryRefreshResult.Updated"/> classification. Deliberately not record
    /// equality: <see cref="AgentDefinition.LoadedAt"/> is stamped fresh on every parse, so a
    /// byte-for-byte-unchanged manifest would otherwise compare unequal on every single reload.
    /// </summary>
    private static bool DefinitionsAreEquivalent(AgentDefinition a, AgentDefinition b) =>
        a.Id == b.Id
        && a.Name == b.Name
        && a.Description == b.Description
        && a.Category == b.Category
        && a.Domain == b.Domain
        && a.Version == b.Version
        && a.Author == b.Author
        && a.Instructions == b.Instructions
        && a.OrchestrationMode == b.OrchestrationMode
        && a.FilePath == b.FilePath
        && a.BaseDirectory == b.BaseDirectory
        && a.Tags.SequenceEqual(b.Tags)
        && a.Skills.SequenceEqual(b.Skills)
        && a.AllowedTools.SequenceEqual(b.AllowedTools)
        && a.Participants.SequenceEqual(b.Participants)
        && Equals(a.MagenticOptions, b.MagenticOptions);

    /// <summary>
    /// Scans every configured, existing agent path and returns the discovered agents alongside
    /// whether any directory along the way failed to enumerate. That second flag exists solely so
    /// <see cref="RebuildAndReconcile"/> can tell "this agent is genuinely gone" apart from "we
    /// failed to see it this pass" (issue #705 code review) — a distinction the result dictionary
    /// alone cannot make.
    /// </summary>
    private (Dictionary<string, AgentDefinition> Agents, bool HadEnumerationErrors) Discover()
    {
        var resolvedPaths = AgentSearchPathResolver.Resolve(_appConfig.CurrentValue.AI?.Agents, _logger);
        _searchedPaths = resolvedPaths;

        var result = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        var hadEnumerationErrors = false;

        foreach (var rootPath in resolvedPaths)
            DiscoverInDirectory(rootPath, depth: 0, result, ref hadEnumerationErrors);

        _logger.LogInformation(
            "Agent discovery complete: {Count} agents found across {PathCount} path(s)",
            result.Count, resolvedPaths.Count);

        return (result, hadEnumerationErrors);
    }

    private void DiscoverInDirectory(
        string directory,
        int depth,
        Dictionary<string, AgentDefinition> result,
        ref bool hadEnumerationErrors)
    {
        if (depth > MaxSearchDepth)
            return;

        var agentFile = Path.Combine(directory, "AGENT.md");

        if (File.Exists(agentFile))
        {
            try
            {
                var definition = _parser.ParseFromFile(agentFile, directory);
                if (!string.IsNullOrEmpty(definition.Id))
                {
                    // Keep the first agent for a given id and warn on collision (mirrors
                    // SkillMetadataRegistry). Critically, the owned-skill scan runs only for the winning
                    // agent, so two agents that collide on id never merge their private skill namespaces.
                    if (result.TryGetValue(definition.Id, out var existing))
                    {
                        _logger.LogWarning(
                            "Agent ID collision on '{AgentId}': keeping first from {ExistingPath}; ignoring duplicate from {DuplicatePath}",
                            definition.Id, existing.BaseDirectory, directory);
                    }
                    else
                    {
                        result[definition.Id] = definition;
                        _logger.LogDebug("Discovered agent: {AgentId} from {Path}", definition.Id, directory);
                        SyncAgentOwnedSkills(directory, definition.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse agent from {Path}", agentFile);
            }

            // A directory with AGENT.md is an agent — don't recurse into it.
            return;
        }

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(directory))
                DiscoverInDirectory(subDir, depth + 1, result, ref hadEnumerationErrors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate directory: {Path}", directory);
            hadEnumerationErrors = true;
        }
    }

    /// <summary>
    /// Scans an agent's own <c>skills/</c> subdirectory for nested <c>SKILL.md</c> files and replaces
    /// its full set in <see cref="AgentOwnedSkillStore"/> keyed by <paramref name="agentId"/>. These
    /// skills are private to the agent: they are deliberately kept out of the global
    /// <c>SkillMetadataRegistry</c> so they neither leak to other agents nor collide with shared skills.
    /// A missing <c>skills/</c> directory is the common case and is silently skipped; a single malformed
    /// nested skill logs a warning without failing the rest of discovery.
    /// </summary>
    /// <remarks>
    /// A full replace rather than incremental registration (issue #705): on a reload, an agent that
    /// deleted one of its nested <c>SKILL.md</c> files must lose that skill from the store too. Scanning
    /// and re-registering each surviving skill one at a time would leave the deleted one behind forever
    /// — <see cref="AgentOwnedSkillStore.ReplaceAgentSkills"/> swaps in exactly the current set.
    /// </remarks>
    private void SyncAgentOwnedSkills(string agentDirectory, string agentId)
    {
        var skillsRoot = Path.Combine(agentDirectory, "skills");
        var skills = NestedSkillScanner.Scan(skillsRoot, _skillParser, _skillFileReader, _logger).ToList();

        _ownedSkills.ReplaceAgentSkills(agentId, skills);

        foreach (var skill in skills)
        {
            _logger.LogDebug(
                "Discovered agent-owned skill {SkillId} for agent {AgentId}", skill.Id, agentId);
        }
    }
}
