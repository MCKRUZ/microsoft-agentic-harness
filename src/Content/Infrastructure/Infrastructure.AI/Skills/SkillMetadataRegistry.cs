using Application.AI.Common.Interfaces;
using Domain.AI.Skills;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Note: FileAgentSkillLoader and FileAgentSkill from Microsoft.Agents.AI are internal types.
// FileAgentSkillsProvider (public) handles progressive disclosure at runtime via AIContextProviders.
// This registry does its own filesystem walk for metadata-only discovery.

namespace Infrastructure.AI.Skills;

/// <summary>
/// Discovers and caches skill metadata by scanning filesystem directories for SKILL.md files.
/// </summary>
/// <remarks>
/// The framework's <c>FileAgentSkillLoader</c> is internal and not accessible directly.
/// This registry implements its own filesystem walk (up to <see cref="MaxSearchDepth"/> levels)
/// mirroring the same pattern. Skill content discovery at runtime is handled by
/// <c>FileAgentSkillsProvider</c> wired into <c>ChatClientAgentOptions.AIContextProviders</c>.
/// </remarks>
public sealed class SkillMetadataRegistry : ISkillMetadataRegistry
{
    private const int MaxSearchDepth = 3;

    private readonly ILogger<SkillMetadataRegistry> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly SkillMetadataParser _parser;

    private Dictionary<string, SkillDefinition>? _cache;
    private IReadOnlyList<string> _searchedPaths = [];
    private readonly Lock _lock = new();

    public SkillMetadataRegistry(
        ILogger<SkillMetadataRegistry> logger,
        IOptionsMonitor<AppConfig> appConfig,
        SkillMetadataParser parser)
    {
        _logger = logger;
        _appConfig = appConfig;
        _parser = parser;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _searchedPaths;

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetAll()
    {
        EnsureLoaded();
        return [.. _cache!.Values];
    }

    /// <inheritdoc />
    public SkillDefinition? TryGet(string skillId)
    {
        EnsureLoaded();
        _cache!.TryGetValue(skillId, out var skill);
        return skill;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByCategory(string category)
    {
        EnsureLoaded();
        return _cache!.Values
            .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByTags(IEnumerable<string> tags)
    {
        EnsureLoaded();
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return _cache!.Values
            .Where(s => s.Tags.Any(t => tagSet.Contains(t)))
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

    private Dictionary<string, SkillDefinition> Discover()
    {
        var skillsConfig = _appConfig.CurrentValue.AI?.Skills;
        var paths = skillsConfig?.AllPaths.ToList() ?? [];

        if (paths.Count == 0)
        {
            _logger.LogInformation("No skill paths configured in AppConfig.AI.Skills — skipping skill discovery");
            _searchedPaths = [];
            return new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        // Resolve relative paths to absolute; skip non-existent
        var resolvedPaths = new List<string>();
        foreach (var p in paths)
        {
            var abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(p);
            if (Directory.Exists(abs))
                resolvedPaths.Add(abs);
            else
                _logger.LogWarning("Skill path not found, skipping: {Path}", abs);
        }

        _searchedPaths = resolvedPaths;

        if (resolvedPaths.Count == 0)
        {
            _logger.LogWarning("No valid skill paths found — skill discovery produced no results");
            return new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootPath in resolvedPaths)
            DiscoverInDirectory(rootPath, rootPath, depth: 0, result);

        _logger.LogInformation(
            "Skill discovery complete: {Count} skills found across {PathCount} path(s)",
            result.Count, resolvedPaths.Count);

        return result;
    }

    private void DiscoverInDirectory(
        string directory,
        string rootPath,
        int depth,
        Dictionary<string, SkillDefinition> result)
    {
        if (depth > MaxSearchDepth)
            return;

        var skillFile = Path.Combine(directory, "SKILL.md");

        if (File.Exists(skillFile))
        {
            try
            {
                var definition = _parser.ParseFromFile(skillFile, directory);
                if (!string.IsNullOrEmpty(definition.Id))
                {
                    result[definition.Id] = definition;
                    _logger.LogDebug("Discovered skill: {SkillId} from {Path}", definition.Id, directory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse skill from {Path}", skillFile);
            }

            // A directory with SKILL.md is a skill — don't recurse into it
            return;
        }

        // Recurse into subdirectories to find nested skills
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(directory))
                DiscoverInDirectory(subDir, rootPath, depth + 1, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate directory: {Path}", directory);
        }
    }
}
