using System.Collections.Concurrent;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// Singleton registry of per-conversation <see cref="SemaphoreSlim"/> instances.
/// Ensures that concurrent <c>SendMessage</c> calls on the same conversation are
/// serialized — preventing interleaved token streams or double-append races.
///
/// Must be registered as a singleton; SignalR hub instances are transient, so the
/// dictionary cannot live on the hub class itself.
///
/// Entries are not self-evicting: because callers hold the returned semaphore for
/// the duration of a turn, the registry cannot know when a conversation is finished
/// being used. Eviction is therefore lifecycle-driven — call <see cref="Remove"/>
/// when a conversation is deleted or aged out so the dictionary does not grow
/// without bound on a long-running host.
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

    /// <summary>
    /// Evicts the lock for <paramref name="conversationId"/> from the registry,
    /// disposing it when it is not currently held.
    ///
    /// Call this when a conversation is deleted or idle-expired to release the
    /// associated <see cref="SemaphoreSlim"/>; without it the singleton dictionary
    /// accumulates one entry per conversation ever touched and never shrinks.
    ///
    /// If the lock is currently held (a turn is in flight) the semaphore is removed
    /// from the dictionary but not disposed — disposing a held <see cref="SemaphoreSlim"/>
    /// would corrupt the in-flight wait. The detached instance is reclaimed by GC once
    /// the holder releases it. A subsequent <see cref="GetOrCreate"/> for the same id
    /// creates a fresh, independent lock.
    /// </summary>
    /// <param name="conversationId">The conversation whose lock should be evicted.</param>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> if none existed.</returns>
    public bool Remove(string conversationId)
    {
        if (!_locks.TryRemove(conversationId, out var semaphore))
        {
            return false;
        }

        // CurrentCount == 1 means the binary lock is free (not held). Disposing a
        // held semaphore would break any in-flight WaitAsync, so defer to GC instead.
        if (semaphore.CurrentCount == 1)
        {
            semaphore.Dispose();
        }

        return true;
    }

    /// <summary>
    /// The number of conversation locks currently retained. Exposed for eviction
    /// diagnostics and tests; not part of the locking contract.
    /// </summary>
    public int Count => _locks.Count;
}
