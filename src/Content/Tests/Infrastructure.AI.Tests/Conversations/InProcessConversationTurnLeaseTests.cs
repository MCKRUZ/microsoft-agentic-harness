using Application.AI.Common.Interfaces.AI;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="InProcessConversationTurnLease"/> against the shared
/// <see cref="ConversationTurnLeaseContractTests"/>, plus the two things only this implementation
/// promises: it never loses a lease, and it does not accumulate entries.
/// </summary>
public sealed class InProcessConversationTurnLeaseTests : ConversationTurnLeaseContractTests
{
    private readonly InProcessConversationTurnLease _lease = new();

    /// <inheritdoc />
    protected override IConversationTurnLease Lease => _lease;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to create: with no record to consult, this implementation leases whatever id it is
    /// given. That difference from the durable one is the reason the contract asks the fixture for an
    /// id rather than making one up.
    /// </remarks>
    protected override Task<string> NewConversationAsync() =>
        Task.FromResult($"conv-{Guid.NewGuid():N}");

    [Fact]
    public async Task LeaseLost_IsNeverCancelled()
    {
        // A semaphore this process holds cannot be taken from it, so there is no loss to report and
        // linking this token at a call site costs nothing.
        var id = await NewConversationAsync();
        await using var handle = await _lease.AcquireAsync(id);

        handle.LeaseLost.Should().Be(CancellationToken.None);
    }

    [Fact]
    public async Task ReleasedLease_LeavesNoEntryBehind()
    {
        // The defect this class was written to remove. Its predecessor kept one semaphore per
        // conversation the host had ever seen, evicted only by a public method that documented itself
        // as lifecycle-driven and that no production code called — so on a long-running host the
        // dictionary only ever grew.
        var id = await NewConversationAsync();

        await using (await _lease.AcquireAsync(id))
        {
            _lease.TrackedConversations.Should().Be(1);
        }

        _lease.TrackedConversations.Should().Be(0);
    }

    [Fact]
    public async Task WaiterQueuedBehindAHolder_KeepsTheEntryAliveUntilItLeavesToo()
    {
        // The counterpart risk to eviction: dropping the entry while someone still waits on it would
        // leave that waiter blocked on a semaphore no future acquirer will ever look up.
        var id = await NewConversationAsync();
        var held = await _lease.AcquireAsync(id);
        var queued = _lease.AcquireAsync(id);

        await held.DisposeAsync();

        var second = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        _lease.TrackedConversations.Should().Be(1, "the waiter took the lease over rather than losing it");

        await second.DisposeAsync();
        _lease.TrackedConversations.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentTurnsOnOneConversation_NeverOverlap()
    {
        // What the lease is for, stated as an observable property rather than as a mechanism: at no
        // point are two turns inside the critical section together.
        var id = await NewConversationAsync();
        var inside = 0;
        var overlaps = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            await using var handle = await _lease.AcquireAsync(id);

            if (Interlocked.Increment(ref inside) != 1)
                Interlocked.Increment(ref overlaps);

            await Task.Yield();
            Interlocked.Decrement(ref inside);
        }));

        overlaps.Should().Be(0);

        // Eviction under contention, which is the case that can go wrong: 32 acquirers on one id
        // drive the reserve/evict race repeatedly, so a reference-counting slip shows up here as a
        // retained entry. Acquiring many *distinct* ids would never enter that race at all.
        _lease.TrackedConversations.Should().Be(0);
    }
}
