using System.Collections.Concurrent;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// Singleton registry of per-conversation <see cref="SemaphoreSlim"/> instances.
/// Ensures that concurrent <c>SendMessage</c> calls on the same conversation are
/// serialized — preventing interleaved token streams or double-append races.
///
/// Must be registered as a singleton; SignalR hub instances are transient, so the
/// dictionary cannot live on the hub class itself.
/// </summary>
public sealed class ConversationLockRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Returns the <see cref="SemaphoreSlim"/> for <paramref name="conversationId"/>,
    /// creating it on first access.
    /// </summary>
    public SemaphoreSlim GetOrCreate(string conversationId) =>
        _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
}
