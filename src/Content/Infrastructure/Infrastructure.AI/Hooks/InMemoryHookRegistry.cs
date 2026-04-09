using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Hooks;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Hooks;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IHookRegistry"/>.
/// Stores hook definitions in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by hook ID for O(1) registration and removal.
/// </summary>
public sealed class InMemoryHookRegistry : IHookRegistry
{
    private readonly ConcurrentDictionary<string, HookDefinition> _hooks = new();
    private readonly IPatternMatcher _patternMatcher;
    private readonly ILogger<InMemoryHookRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryHookRegistry"/> class.
    /// </summary>
    /// <param name="patternMatcher">Pattern matcher for tool name glob matching (shared with permission system).</param>
    /// <param name="logger">Logger for registration diagnostics.</param>
    public InMemoryHookRegistry(
        IPatternMatcher patternMatcher,
        ILogger<InMemoryHookRegistry> logger)
    {
        _patternMatcher = patternMatcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Register(HookDefinition hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        if (_hooks.TryAdd(hook.Id, hook))
        {
            _logger.LogDebug(
                "Registered hook {HookId} for event {Event} (type={Type}, priority={Priority})",
                hook.Id, hook.Event, hook.Type, hook.Priority);
        }
        else
        {
            _hooks[hook.Id] = hook;
            _logger.LogDebug(
                "Replaced hook {HookId} for event {Event} (type={Type}, priority={Priority})",
                hook.Id, hook.Event, hook.Type, hook.Priority);
        }
    }

    /// <inheritdoc />
    public bool Unregister(string hookId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);

        var removed = _hooks.TryRemove(hookId, out _);

        if (removed)
            _logger.LogDebug("Unregistered hook {HookId}", hookId);

        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<HookDefinition> GetHooksForEvent(HookEvent hookEvent, string? toolName = null)
    {
        var results = new List<HookDefinition>();

        foreach (var hook in _hooks.Values)
        {
            if (hook.Event != hookEvent)
                continue;

            if (toolName is not null && hook.ToolMatcher is not null
                && !_patternMatcher.IsMatch(hook.ToolMatcher, toolName))
                continue;

            results.Add(hook);
        }

        results.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return results;
    }

}
