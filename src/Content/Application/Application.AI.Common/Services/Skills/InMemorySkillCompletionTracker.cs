using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Skills;

namespace Application.AI.Common.Services.Skills;

/// <summary>
/// In-memory, conversation-scoped implementation of <see cref="ISkillCompletionTracker"/>.
/// Thread-safe for concurrent access across multiple conversation turns.
/// </summary>
/// <remarks>
/// State is held in memory and not persisted. When the process restarts, all completion
/// state is lost. This is intentional — prerequisites are conversation-scoped and don't
/// need to survive restarts.
/// </remarks>
public sealed class InMemorySkillCompletionTracker : ISkillCompletionTracker
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _state = new();
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public void MarkCompleted(string conversationId, string skillId)
    {
        lock (_lock)
        {
            var set = _state.GetOrAdd(conversationId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            set.Add(skillId);
        }
    }

    /// <inheritdoc />
    public bool IsCompleted(string conversationId, string skillId)
    {
        lock (_lock)
        {
            return _state.TryGetValue(conversationId, out var set) && set.Contains(skillId);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetCompletedSkills(string conversationId)
    {
        lock (_lock)
        {
            if (_state.TryGetValue(conversationId, out var set))
                return new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public void ClearConversation(string conversationId)
    {
        _state.TryRemove(conversationId, out _);
    }
}
