using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// Thread-safe in-memory cache for computed system prompt sections.
/// Uses a composite key of (agentId, sectionType) to support independent
/// per-type invalidation without clearing unrelated sections.
/// </summary>
public sealed class InMemoryPromptSectionCache : IPromptSectionCache
{
    private readonly ConcurrentDictionary<(string AgentId, SystemPromptSectionType Type), SystemPromptSection> _cache = new();

    /// <inheritdoc />
    public bool TryGet(string agentId, SystemPromptSectionType type, out SystemPromptSection? section)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        if (_cache.TryGetValue((agentId, type), out var cached))
        {
            section = cached;
            return true;
        }

        section = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string agentId, SystemPromptSection section)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(section);

        _cache[(agentId, section.Type)] = section;
    }

    /// <inheritdoc />
    public void Invalidate(SystemPromptSectionType type)
    {
        var keysToRemove = _cache.Keys.Where(k => k.Type == type).ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _cache.Clear();
    }
}
