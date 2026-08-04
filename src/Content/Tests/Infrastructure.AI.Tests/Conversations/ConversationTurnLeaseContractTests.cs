using Application.AI.Common.Interfaces.AI;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The behaviour every <see cref="IConversationTurnLease"/> implementation owes its callers,
/// asserted against each of them.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the two implementations exist to be interchangeable: a host
/// swaps one for the other by changing which conversation store it uses, and a difference between
/// them shows up as turns interleaving under one provider and not the other. The same reasoning that
/// put both stores behind <c>ConversationStoreContractTests</c>.
/// </remarks>
public abstract class ConversationTurnLeaseContractTests
{
    /// <summary>How long a test waits for something that should happen promptly before failing.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>How long a test waits to satisfy itself that something is <em>not</em> happening.</summary>
    private static readonly TimeSpan Impatience = TimeSpan.FromMilliseconds(300);

    /// <summary>The implementation under test.</summary>
    protected abstract IConversationTurnLease Lease { get; }

    /// <summary>
    /// Produces a conversation id this implementation will lease. The durable implementation refuses
    /// an id with no conversation behind it, so the fixture has to create one.
    /// </summary>
    protected abstract Task<string> NewConversationAsync();

    [Fact]
    public async Task AcquireAsync_BlankConversationId_Throws()
    {
        // Blank means "some conversation, unspecified". Leasing that would serialise every caller
        // who passed a blank id against each other and none of them against the real conversation.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Lease.AcquireAsync("  "));
    }

    [Fact]
    public async Task AcquireAsync_WhenFree_ReturnsImmediately()
    {
        var id = await NewConversationAsync();

        await using var handle = await Lease.AcquireAsync(id).WaitAsync(Patience);

        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_WhileHeld_WaitsUntilReleased()
    {
        // The whole point of the abstraction. If this passes with the lease removed, nothing here
        // is being tested.
        var id = await NewConversationAsync();
        var first = await Lease.AcquireAsync(id);

        var second = Lease.AcquireAsync(id);

        await Task.Delay(Impatience);
        second.IsCompleted.Should().BeFalse("a second turn must wait for the one in flight");

        await first.DisposeAsync();

        await using var handle = await second.WaitAsync(Patience);
        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_DifferentConversations_DoNotBlockEachOther()
    {
        // A lease per conversation, not one lock over all of them: otherwise every user's turn queues
        // behind every other user's.
        var first = await NewConversationAsync();
        var second = await NewConversationAsync();

        await using var firstHandle = await Lease.AcquireAsync(first);
        await using var secondHandle = await Lease.AcquireAsync(second).WaitAsync(Patience);

        secondHandle.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_Throws()
    {
        var id = await NewConversationAsync();
        await using var held = await Lease.AcquireAsync(id);

        using var cts = new CancellationTokenSource();
        var queued = Lease.AcquireAsync(id, cts.Token);

        await Task.Delay(Impatience);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
    }

    [Fact]
    public async Task AcquireAsync_AfterAWaiterGivesUp_StillSucceedsForTheNextCaller()
    {
        // A cancelled waiter has to leave nothing behind. The in-process implementation reference
        // counts its entries, so an abandoned wait that forgets to decrement pins the entry; the
        // durable one has nothing to leave behind but is asserted here so the contract is one rule.
        var id = await NewConversationAsync();
        var held = await Lease.AcquireAsync(id);

        using (var cts = new CancellationTokenSource())
        {
            var abandoned = Lease.AcquireAsync(id, cts.Token);
            await Task.Delay(Impatience);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Patience));
        }

        await held.DisposeAsync();

        await using var next = await Lease.AcquireAsync(id).WaitAsync(Patience);
        next.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotReleaseTheLeaseTwice()
    {
        // `await using` plus an explicit dispose on a failure path is an easy accident. If the second
        // release counted, two turns would be let in at once — the exact failure the lease prevents.
        var id = await NewConversationAsync();
        var handle = await Lease.AcquireAsync(id);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        await using var reacquired = await Lease.AcquireAsync(id).WaitAsync(Patience);
        var third = Lease.AcquireAsync(id);

        await Task.Delay(Impatience);
        third.IsCompleted.Should().BeFalse("the double dispose must not have left a spare slot");

        await reacquired.DisposeAsync();
        await (await third.WaitAsync(Patience)).DisposeAsync();
    }

    [Fact]
    public async Task Handle_ReleasedNormally_LeavesLeaseLostUncancelled()
    {
        // Loss is not the same as ending. A handle that cancelled its own token on release would make
        // every turn look like it had been taken over.
        var id = await NewConversationAsync();
        var handle = await Lease.AcquireAsync(id);

        handle.LeaseLost.IsCancellationRequested.Should().BeFalse();

        await handle.DisposeAsync();
    }
}
