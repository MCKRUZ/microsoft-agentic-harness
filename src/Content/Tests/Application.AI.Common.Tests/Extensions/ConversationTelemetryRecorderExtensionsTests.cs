using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// <see cref="ConversationTelemetryRecorderExtensions.RecordFailedTurnAsync"/> (#780): a failed or
/// cancelled turn that spent anything reaches the session rollup, one that spent nothing does not, the
/// write never runs under the caller's token, and a turn whose lease was lost is not written at all.
/// </summary>
public sealed class ConversationTelemetryRecorderExtensionsTests : IAsyncLifetime
{
    private readonly Mock<IConversationTurnLeaseHandle> _handle = new();
    private readonly CancellationTokenSource _leaseLost = new();
    private readonly Mock<IConversationTelemetryRecorder> _recorder = new();
    private LeasedTurn _leased = null!;

    private static readonly ConversationTelemetryState Before = new(
        "c1", "user-1", Guid.NewGuid(), TelemetryAccumulator.Zero, SessionOpened: false);

    private static ConversationTurnTelemetry Spent => new(900, 100, 25, 5, 0.04m, ToolCalls: 0, Model: "m");

    public async Task InitializeAsync()
    {
        _handle.SetupGet(h => h.LeaseLost).Returns(_leaseLost.Token);
        var lease = new Mock<IConversationTurnLease>();
        lease.Setup(l => l.AcquireAsync("c1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(_handle.Object);
        _leased = await LeasedTurn.AcquireAsync(lease.Object, "c1", CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _leased.DisposeAsync();
        _leaseLost.Dispose();
    }

    [Fact]
    public async Task RecordFailedTurnAsync_TurnSpentSomething_RecordsItAndReturnsTheUpdatedState()
    {
        var after = Before with { Totals = Before.Totals.Add(900, 100, 25, 5, 0.04m, 0) };
        _recorder
            .Setup(r => r.RecordTurnAsync(Before, Spent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(after);

        var result = await _recorder.Object.RecordFailedTurnAsync(Before, Spent, _leased);

        result.Should().BeSameAs(after);
    }

    [Fact]
    public async Task RecordFailedTurnAsync_TurnSpentNothing_LeavesTheRollupAlone()
    {
        var result = await _recorder.Object.RecordFailedTurnAsync(
            Before, new ConversationTurnTelemetry(0, 0, 0, 0, 0m, ToolCalls: 0), _leased);

        result.Should().BeSameAs(Before);
        VerifyNothingRecorded();
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(0, 1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(0, 0, 0, 1, 0)]
    [InlineData(0, 0, 0, 0, 1)]
    public async Task RecordFailedTurnAsync_AnySingleKindOfSpend_IsEnoughToRecord(
        int input, int output, int cacheRead, int cacheWrite, int costCents)
    {
        var turn = new ConversationTurnTelemetry(input, output, cacheRead, cacheWrite, costCents / 100m, ToolCalls: 0);

        await _recorder.Object.RecordFailedTurnAsync(Before, turn, _leased);

        _recorder.Verify(
            r => r.RecordTurnAsync(Before, turn, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordFailedTurnAsync_NeverRunsTheWriteUnderACancellableToken()
    {
        // A client that disconnects cancels the token the turn ran under; the same token on the write
        // would drop the record of exactly the turn that was cut short.
        CancellationToken observed = new(canceled: true);
        _recorder
            .Setup(r => r.RecordTurnAsync(
                It.IsAny<ConversationTelemetryState>(), It.IsAny<ConversationTurnTelemetry>(),
                It.IsAny<CancellationToken>()))
            .Callback((ConversationTelemetryState _, ConversationTurnTelemetry _, CancellationToken ct) => observed = ct)
            .ReturnsAsync(Before);

        await _recorder.Object.RecordFailedTurnAsync(Before, Spent, _leased);

        observed.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task RecordFailedTurnAsync_LeaseWasLost_DoesNotWriteTheRollup()
    {
        // The totals are written absolute from this run's baseline, so a host that no longer holds the
        // conversation would overwrite what the host that does hold it has recorded since.
        await _leaseLost.CancelAsync();

        var result = await _recorder.Object.RecordFailedTurnAsync(Before, Spent, _leased);

        result.Should().BeSameAs(Before);
        VerifyNothingRecorded();
    }

    [Fact]
    public async Task RecordFailedTurnAsync_NoLease_RecordsAsNormal()
    {
        // A self-contained run holds no lease, and has no durable conversation to lose.
        await _recorder.Object.RecordFailedTurnAsync(Before, Spent, leased: null);

        _recorder.Verify(
            r => r.RecordTurnAsync(Before, Spent, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyNothingRecorded() =>
        _recorder.Verify(
            r => r.RecordTurnAsync(
                It.IsAny<ConversationTelemetryState>(), It.IsAny<ConversationTurnTelemetry>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
}
