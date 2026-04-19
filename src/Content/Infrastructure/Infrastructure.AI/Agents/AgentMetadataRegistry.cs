using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.Common.Config;
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
/// </remarks>
public sealed class AgentMetadataRegistry : IAgentMetadataRegistry
{
    private const int MaxSearchDepth = 3;

    private readonly ILogger<AgentMetadataRegistry> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly AgentMetadataParser _parser;

    private Dictionary<string, AgentDefinition>? _cache;
    private IReadOnlyList<string> _searchedPaths = [];
    private readonly Lock _lock = new();

    /// <summary>Initialises the registry with its dependencies.</summary>
    public AgentMetadataRegistry(
        ILogger<AgentMetadataRegistry> logger,
        IOptionsMonitor<AppConfig> appConfig,
        AgentMetadataParser parser)
    {
        _logger = logger;
        _appConfig = appConfig;
        _parser = parser;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _searchedPaths;

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetAll()
    {
        EnsureLoaded();
        return [.. _cache!.Values];
    }

    /// <inheritdoc />
    public AgentDefinition? TryGet(string agentId)
    {
        EnsureLoaded();
        _cache!.TryGetValue(agentId, out var agent);
        return agent;
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByCategory(string category)
    {
        EnsureLoaded();
        return _cache!.Values
            .Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByTags(IEnumerable<string> tags)
    {
        EnsureLoaded();
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return _cache!.Values
            .Where(a => a.Tags.Any(t => tagSet.Contains(t)))
            .ToList();
    }

    private void EnsureLoaded()
    {
        if (_cache is not null)
            return;

        lock (_lock)
        {
            if (_cache is not null)
                return;

            _cache = Discover();
        }
    }

    private Dictionary<string, AgentDefinition> Discover()
    {
        var agentsConfig = _appConfig.CurrentValue.AI?.Agents;
        var paths = agentsConfig?.AllPaths.ToList() ?? [];

        if (paths.Count == 0)
        {
            _logger.LogInformation("No agent paths configured in AppConfig.AI.Agents — skipping agent discovery");
            _searchedPaths = [];
            return new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        // Resolve relative paths against AppContext.BaseDirectory (the bin folder)
        // so they match where csproj Content Include copies skills/agents at build time.
        // Avoids coupling configured paths to the process CWD, which differs between
        // `dotnet run` (project dir) and a published deployment (publish dir).
        var resolvedPaths = new List<string>();
        foreach (var p in paths)
        {
            var abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(p, AppContext.BaseDirectory);
            if (Directory.Exists(abs))
                resolvedPaths.Add(abs);
            else
                _logger.LogWarning("Agent path not found, skipping: {Path}", abs);
        }

        _searchedPaths = resolvedPaths;

        if (resolvedPaths.Count == 0)
        {
            _logger.LogWarning("No valid agent paths found — agent discovery produced no results");
            return new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootPath in resolvedPaths)
            DiscoverInDirectory(rootPath, depth: 0, result);

        _logger.LogInformation(
            "Agent discovery complete: {Count} agents found across {PathCount} path(s)",
            result.Count, resolvedPaths.Count);

        return result;
    }

    private void DiscoverInDirectory(
        string directory,
        int depth,
        Dictionary<string, AgentDefinition> result)
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
                    result[definition.Id] = definition;
                    _logger.LogDebug("Discovered agent: {AgentId} from {Path}", definition.Id, directory);
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
                DiscoverInDirectory(subDir, depth + 1, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate directory: {Path}", directory);
        }
    }
}
