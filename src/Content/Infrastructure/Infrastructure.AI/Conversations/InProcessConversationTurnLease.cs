using Application.AI.Common.Interfaces.AI;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Single-process turn lease: one <see cref="SemaphoreSlim"/> per conversation, held for the duration
/// of a turn. Paired with <see cref="FileSystemConversationStore"/>, which is itself only safe in one
/// process, so a lease that reaches no further is the matching guarantee rather than a shortfall.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Registered as a singleton.</strong> The whole mechanism is the shared lock table; several
/// instances would each hand out their own semaphore for the same conversation and serialise nothing.
/// </para>
/// <para>
/// <strong>Entries evict themselves.</strong> This replaces <c>ConversationLockRegistry</c>, whose
/// eviction was a public <c>Remove</c> method that documented itself as lifecycle-driven and that no
/// production code ever called — so the dictionary grew by one entry per conversation the host had
/// ever seen and never shrank. The table is a <see cref="KeyedAsyncLock"/>, which reference-counts an
/// entry by its holder and its waiters and drops it when the last of them leaves, so no caller has to
/// remember anything.
/// </para>
/// <para>
/// <see cref="IConversationTurnLeaseHandle.LeaseLost"/> is always
/// <see cref="CancellationToken.None"/>: a semaphore held in this process cannot be taken from it, so
/// there is no loss to report.
/// </para>
/// </remarks>
public sealed class InProcessConversationTurnLease : IConversationTurnLease
{
    private readonly KeyedAsyncLock _locks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IConversationTurnLeaseHandle> AcquireAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        return new Handle(await _locks.AcquireAsync(conversationId, ct));
    }

    /// <summary>
    /// The number of conversations currently holding or waiting for a lease. Exposed for eviction
    /// tests and diagnostics; not part of the lease contract.
    /// </summary>
    internal int TrackedConversations => _locks.TrackedKeys;

    /// <summary>The held lease. Releasing it releases the semaphore and may evict the entry.</summary>
    private sealed class Handle(IDisposable held) : IConversationTurnLeaseHandle
    {
        /// <inheritdoc />
        public CancellationToken LeaseLost => CancellationToken.None;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            held.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
