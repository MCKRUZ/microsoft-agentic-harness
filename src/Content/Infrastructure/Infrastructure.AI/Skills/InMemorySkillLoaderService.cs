using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// In-memory implementation of <see cref="ISkillLoaderService"/> that satisfies DI requirements.
/// Skills discovered from the filesystem are cached in memory. For the current POC, the built-in
/// agents load their instructions from embedded SKILL.md resources via <c>AgentDefinitions</c>,
/// so this service primarily supports the prompt section providers and future skill discovery.
/// </summary>
public sealed class InMemorySkillLoaderService : ISkillLoaderService
{
    private readonly Dictionary<string, SkillDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemorySkillLoaderService> _logger;
    private readonly object _lock = new();

    public InMemorySkillLoaderService(ILogger<InMemorySkillLoaderService> logger)
    {
        _logger = logger;
    }

    public bool IsWatching => false;

    public event EventHandler<SkillChangedEventArgs>? SkillChanged;

    #region Core Loading

    public Task<SkillDefinition> LoadSkillAsync(string skillId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(skillId, out var skill))
                return Task.FromResult(skill);
        }

        throw new SkillNotFoundException(skillId);
    }

    public Task<SkillDefinition> LoadSkillFileFromPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("LoadSkillFileFromPathAsync not yet implemented for path: {Path}", filePath);
        throw new SkillNotFoundException(filePath);
    }

    public Task<SkillDefinition?> TryLoadSkillAsync(string skillId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _cache.TryGetValue(skillId, out var skill);
            return Task.FromResult(skill);
        }
    }

    #endregion

    #region Discovery

    public Task<IReadOnlyList<string>> DiscoverSkillIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_cache.Keys.ToList());
        }
    }

    public Task<IReadOnlyList<SkillDefinition>> DiscoverSkillsAsync(
        Func<SkillDefinition, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IEnumerable<SkillDefinition> skills = _cache.Values;
            if (filter != null)
                skills = skills.Where(filter);
            return Task.FromResult<IReadOnlyList<SkillDefinition>>(skills.ToList());
        }
    }

    public Task<IReadOnlyList<SkillDefinition>> DiscoverByCategoryAsync(
        string category, CancellationToken cancellationToken = default)
    {
        return DiscoverSkillsAsync(s =>
            string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase), cancellationToken);
    }

    public Task<IReadOnlyList<SkillDefinition>> DiscoverByTagsAsync(
        IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return DiscoverSkillsAsync(s =>
            s.Tags.Any(t => tagSet.Contains(t)), cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var categories = _cache.Values
                .Select(s => s.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(categories);
        }
    }

    public Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var allTags = _cache.Values
                .SelectMany(s => s.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(allTags);
        }
    }

    public bool SkillExists(string skillId)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(skillId);
        }
    }

    public Task<bool> SkillExistsAsync(string skillId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SkillExists(skillId));
    }

    #endregion

    #region Resource Loading

    public Task<string?> TryLoadTemplateAsync(string skillId, string templateName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<string> LoadTemplateAsync(string skillId, string templateName, CancellationToken cancellationToken = default)
    {
        throw new SkillNotFoundException($"Template '{templateName}' not found for skill '{skillId}'");
    }

    public Task<string> LoadReferenceAsync(string skillId, string referenceName, CancellationToken cancellationToken = default)
    {
        throw new SkillNotFoundException($"Reference '{referenceName}' not found for skill '{skillId}'");
    }

    public Task<IDictionary<string, string>> LoadAllTemplatesAsync(string skillId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());
    }

    public Task<IDictionary<string, string>> LoadAllReferencesAsync(string skillId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());
    }

    #endregion

    #region Cache Management

    public void ClearCache()
    {
        lock (_lock)
        {
            _cache.Clear();
            _logger.LogInformation("Skill cache cleared");
        }
    }

    public void ClearFromCache(string skillId)
    {
        lock (_lock)
        {
            _cache.Remove(skillId);
        }
    }

    public Task<int> PreloadAllSkillsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_cache.Count);
        }
    }

    public SkillCacheStatistics GetCacheStatistics()
    {
        lock (_lock)
        {
            return new SkillCacheStatistics(_cache.Count, 0, 0, null);
        }
    }

    #endregion

    #region File Watching

    public void StartWatching()
    {
        _logger.LogDebug("File watching not yet implemented");
    }

    public void StopWatching()
    {
        // No-op
    }

    #endregion
}
