using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.AI;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Single-process turn lease: one <see cref="SemaphoreSlim"/> per conversation, held for the duration
/// of a turn. Paired with <see cref="FileSystemConversationStore"/>, which is itself only safe in one
/// process, so a lease that reaches no further is the matching guarantee rather than a shortfall.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Registered as a singleton.</strong> The whole mechanism is the shared dictionary; several
/// instances would each hand out their own semaphore for the same conversation and serialise nothing.
/// </para>
/// <para>
/// <strong>Entries evict themselves.</strong> This replaces <c>ConversationLockRegistry</c>, whose
/// eviction was a public <c>Remove</c> method that documented itself as lifecycle-driven and that no
/// production code ever called — so the dictionary grew by one entry per conversation the host had
/// ever seen and never shrank. Here an entry is reference-counted by its holder and its waiters and
/// is dropped when the last of them leaves, which needs no caller to remember anything.
/// </para>
/// <para>
/// <see cref="IConversationTurnLeaseHandle.LeaseLost"/> is always
/// <see cref="CancellationToken.None"/>: a semaphore held in this process cannot be taken from it, so
/// there is no loss to report.
/// </para>
/// </remarks>
public sealed class InProcessConversationTurnLease : IConversationTurnLease
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IConversationTurnLeaseHandle> AcquireAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var entry = Reserve(conversationId);

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            // Cancelled while queued. The reservation has to come back off, or an abandoned wait
            // leaves the entry pinned in the dictionary for the lifetime of the host.
            Unreserve(conversationId, entry);
            throw;
        }

        return new Handle(this, conversationId, entry);
    }

    /// <summary>
    /// The number of conversations currently holding or waiting for a lease. Exposed for eviction
    /// tests and diagnostics; not part of the lease contract.
    /// </summary>
    internal int TrackedConversations => _entries.Count;

    /// <summary>
    /// Takes a reference on the conversation's entry, creating it if needed.
    /// </summary>
    /// <remarks>
    /// The retry loop closes the window between <c>GetOrAdd</c> handing back an entry and this thread
    /// taking its reference: a releasing thread may evict that entry in between. Eviction sets
    /// <see cref="Entry.Evicted"/> under the entry's own lock, so a reserver that loses the race sees
    /// the flag and starts again against whatever is in the dictionary now — rather than waiting on a
    /// semaphore no future acquirer will ever look up.
    ///
    /// What makes the retry <em>terminate</em> is that eviction removes the entry from the dictionary
    /// under that same lock, so the next <c>GetOrAdd</c> cannot hand back the flagged one. Flagging
    /// without removing turns this into a spin that never ends — measured, not theorised: dropping
    /// the <c>TryRemove</c> hangs the test suite rather than failing an assertion.
    /// </remarks>
    private Entry Reserve(string conversationId)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(conversationId, static _ => new Entry());

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
    private void Unreserve(string conversationId, Entry entry)
    {
        lock (entry.Gate)
        {
            if (--entry.References > 0)
                return;

            entry.Evicted = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(conversationId, entry));
            entry.Semaphore.Dispose();
        }
    }

    /// <summary>Per-conversation semaphore plus the reference count that decides when it is dropped.</summary>
    private sealed class Entry
    {
        /// <summary>Guards <see cref="References"/> and <see cref="Evicted"/>. Never held across an await.</summary>
        public object Gate { get; } = new();

        /// <summary>The binary lock serialising turns on this conversation.</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders plus waiters. Guarded by <see cref="Gate"/>.</summary>
        public int References { get; set; }

        /// <summary>Set once this entry has left the dictionary, so a racing reserver retries.</summary>
        public bool Evicted { get; set; }
    }

    /// <summary>The held lease. Releasing it releases the semaphore and may evict the entry.</summary>
    private sealed class Handle : IConversationTurnLeaseHandle
    {
        private readonly InProcessConversationTurnLease _owner;
        private readonly string _conversationId;
        private readonly Entry _entry;
        private int _released;

        public Handle(InProcessConversationTurnLease owner, string conversationId, Entry entry)
        {
            _owner = owner;
            _conversationId = conversationId;
            _entry = entry;
        }

        /// <inheritdoc />
        public CancellationToken LeaseLost => CancellationToken.None;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            // Idempotent: a call site with both `await using` and an explicit dispose in a failure
            // path would otherwise release the semaphore twice and let two turns in at once.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return ValueTask.CompletedTask;

            // Release before unreserving. Unreserving can dispose the semaphore, and disposing one
            // that has not been released loses the slot for every future acquirer of a re-created
            // entry — which is a deadlock, not a leak.
            _entry.Semaphore.Release();
            _owner.Unreserve(_conversationId, _entry);
            return ValueTask.CompletedTask;
        }
    }
}
