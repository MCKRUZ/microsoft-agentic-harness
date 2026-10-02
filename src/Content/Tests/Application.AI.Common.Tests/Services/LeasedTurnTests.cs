using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Pins the one thing <see cref="LeasedTurn"/> exists to centralise: the lost-lease signal reaches
/// the token a turn runs under, and a lost lease is told apart from a client disconnect the same
/// way for every transport. Each of those used to be hand-written at three call sites.
/// </summary>
public sealed class LeasedTurnTests
{
    [Fact]
    public async Task Token_LeaseLost_IsCancelledAndReportedAsLeaseLost()
    {
        var lease = new FakeLease();
        await using var turn = await LeasedTurn.AcquireAsync(lease, "c1", CancellationToken.None);

        lease.Steal();

        turn.Token.IsCancellationRequested.Should().BeTrue(
            "a lease another host took must stop the turn that thinks it still holds it");
        turn.LeaseWasLost.Should().BeTrue();
    }

    [Fact]
    public async Task Token_CallerCancels_IsCancelledButNotReportedAsLeaseLost()
    {
        var lease = new FakeLease();
        using var caller = new CancellationTokenSource();
        await using var turn = await LeasedTurn.AcquireAsync(lease, "c1", caller.Token);

        await caller.CancelAsync();

        turn.Token.IsCancellationRequested.Should().BeTrue();
        turn.LeaseWasLost.Should().BeFalse(
            "a client disconnect must not be reported as another host taking the lease");
    }

    [Fact]
    public async Task LeaseWasLost_BothCallerAndLeaseCancelled_ReportsTheDisconnect()
    {
        var lease = new FakeLease();
        using var caller = new CancellationTokenSource();
        await using var turn = await LeasedTurn.AcquireAsync(lease, "c1", caller.Token);

        lease.Steal();
        await caller.CancelAsync();

        turn.LeaseWasLost.Should().BeFalse(
            "when the caller has also gone, the disconnect is the honest explanation");
    }

    [Fact]
    public async Task LeaseWasLost_NothingCancelled_IsFalse()
    {
        var lease = new FakeLease();
        await using var turn = await LeasedTurn.AcquireAsync(lease, "c1", CancellationToken.None);

        turn.Token.IsCancellationRequested.Should().BeFalse();
        turn.LeaseWasLost.Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_PassesTheConversationAndCallerTokenToTheLease()
    {
        var lease = new FakeLease();
        using var caller = new CancellationTokenSource();

        await using var turn = await LeasedTurn.AcquireAsync(lease, "conversation-7", caller.Token);

        lease.AcquiredFor.Should().Be("conversation-7");
        lease.WaitToken.Should().Be(caller.Token,
            "the caller's token is what cancels the wait while another turn holds the lease");
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheLease()
    {
        var lease = new FakeLease();
        var turn = await LeasedTurn.AcquireAsync(lease, "c1", CancellationToken.None);
        lease.Released.Should().BeFalse("the lease is held for the life of the scope");

        await turn.DisposeAsync();

        lease.Released.Should().BeTrue();
    }

    private sealed class FakeLease : IConversationTurnLease
    {
        private readonly CancellationTokenSource _lost = new();

        public string? AcquiredFor { get; private set; }
        public CancellationToken WaitToken { get; private set; }
        public bool Released { get; private set; }

        public void Steal() => _lost.Cancel();

        public Task<IConversationTurnLeaseHandle> AcquireAsync(
            string conversationId, CancellationToken ct = default)
        {
            AcquiredFor = conversationId;
            WaitToken = ct;
            return Task.FromResult<IConversationTurnLeaseHandle>(new Handle(this));
        }

        private sealed class Handle(FakeLease owner) : IConversationTurnLeaseHandle
        {
            public CancellationToken LeaseLost => owner._lost.Token;

            public ValueTask DisposeAsync()
            {
                owner.Released = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
