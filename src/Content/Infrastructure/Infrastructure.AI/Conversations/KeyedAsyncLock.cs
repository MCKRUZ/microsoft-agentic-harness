using System.Collections.Concurrent;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// One <see cref="SemaphoreSlim"/> per key, held across <c>await</c>s, whose entries evict themselves
/// when the last holder or waiter leaves.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the two single-process pieces that serialise per conversation —
/// <see cref="InProcessConversationTurnLease"/> and <see cref="FileSystemConversationStore"/> — so the
/// reference-counting that makes eviction safe lives in one place rather than being re-derived in
/// each. <c>FileSystemHarnessCandidateRepository</c> still carries its own copy of the algorithm.
/// </para>
/// <para>
/// <strong>Singleton per purpose.</strong> The whole mechanism is the shared dictionary; two instances
/// would each hand out their own semaphore for the same key and serialise nothing. Not reentrant —
/// acquiring a key this caller already holds waits on itself.
/// </para>
/// </remarks>
internal sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, Entry> _entries;

    /// <param name="comparer">
    /// How two keys are judged to name the same lock. Case-insensitive for file paths on a filesystem
    /// that folds case, ordinal for opaque ids.
    /// </param>
    public KeyedAsyncLock(StringComparer comparer) => _entries = new(comparer);

    /// <summary>
    /// The number of keys currently held or waited for. Exposed for eviction tests and diagnostics;
    /// not part of the locking contract.
    /// </summary>
    internal int TrackedKeys => _entries.Count;

    /// <summary>Waits until this caller holds <paramref name="key"/>, then returns the holder to dispose.</summary>
    /// <param name="key">The thing being serialised.</param>
    /// <param name="ct">Cancels the wait. Once the key is held, it no longer affects it.</param>
    /// <returns>The held key. Disposing it releases the key; disposing it twice releases it once.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var entry = Reserve(key);

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            // Cancelled while queued. The reservation has to come back off, or an abandoned wait
            // leaves the entry pinned in the dictionary for the lifetime of the host.
            Unreserve(key, entry);
            throw;
        }

        return new Holder(this, key, entry);
    }

    /// <summary>
    /// Takes a reference on the key's entry, creating it if needed.
    /// </summary>
    /// <remarks>
    /// The retry loop closes the window between <c>GetOrAdd</c> handing back an entry and this thread
    /// taking its reference: a releasing thread may evict that entry in between. Eviction sets
    /// <see cref="Entry.Evicted"/> under the entry's own lock, so a reserver that loses the race sees
    /// the flag and starts again against whatever is in the dictionary now — rather than waiting on a
    /// semaphore no future acquirer will ever look up. The retry terminates because eviction also
    /// removes the entry under that same lock; flagging without removing would spin forever.
    /// </remarks>
    private Entry Reserve(string key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, static _ => new Entry());

            lock (entry.Gate)
            {
                if (!entry.Evicted)
                {
                    entry.References++;
                    return entry;
                }
            }
        }
    }

    /// <summary>
    /// Drops a reference and evicts the entry once nothing holds or awaits it.
    /// </summary>
    /// <remarks>
    /// Disposing the semaphore here is safe precisely because the count reached zero: no thread holds
    /// it and none is waiting on it, and a thread that is about to wait is still blocked on
    /// <see cref="Entry.Gate"/> in <see cref="Reserve"/> and will see the eviction flag instead. The
    /// key-and-value overload of <c>TryRemove</c> matters — removing by key alone could delete a
    /// successor entry that a concurrent reserver has already published.
    /// </remarks>
    private void Unreserve(string key, Entry entry)
    {
        lock (entry.Gate)
        {
            if (--entry.References > 0)
                return;

            entry.Evicted = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            entry.Semaphore.Dispose();
        }
    }

    /// <summary>Per-key semaphore plus the reference count that decides when it is dropped.</summary>
    private sealed class Entry
    {
        /// <summary>Guards <see cref="References"/> and <see cref="Evicted"/>. Never held across an await.</summary>
        public object Gate { get; } = new();

        /// <summary>The binary lock serialising this key.</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders plus waiters. Guarded by <see cref="Gate"/>.</summary>
        public int References { get; set; }

        /// <summary>Set once this entry has left the dictionary, so a racing reserver retries.</summary>
        public bool Evicted { get; set; }
    }

    /// <summary>The held key. Releasing it releases the semaphore and may evict the entry.</summary>
    private sealed class Holder(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Idempotent: a call site with both a using and an explicit dispose in a failure path would
            // otherwise release the semaphore twice and let two holders in at once.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            // Release before unreserving. Unreserving can dispose the semaphore, and disposing one
            // that has not been released loses the slot for every future acquirer of a re-created
            // entry — which is a deadlock, not a leak.
            entry.Semaphore.Release();
            owner.Unreserve(key, entry);
        }
    }
}
