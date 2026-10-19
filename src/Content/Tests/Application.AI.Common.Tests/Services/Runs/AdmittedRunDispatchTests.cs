using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using FluentAssertions;
using Moq;

namespace Application.AI.Common.Tests.Services.Runs;

/// <summary>
/// Tests for <see cref="AdmittedRunDispatch"/> (#731), the one place every run-starting path hands an
/// admitted run to the dispatcher. The branch that matters most, queuing failing, was untested at all
/// three of the copies this replaced.
/// </summary>
public sealed class AdmittedRunDispatchTests
{
    private static readonly DateTimeOffset FailedAt = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IRunDispatchQueue> _queue = new();
    private readonly Mock<IRunJobStore> _store = new();

    private static RunRecord Admitted() => new()
    {
        JobId = "job-1",
        Kind = RunKind.Workflow,
        TargetId = "target-1",
        OwnerId = "owner-1",
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = FailedAt.AddMinutes(-1)
    };

    [Fact]
    public async Task QueueOrAbandonAsync_QueueAccepts_ReturnsNullAndLeavesTheRecordAlone()
    {
        var result = await AdmittedRunDispatch.QueueOrAbandonAsync(_queue.Object, _store.Object, Admitted(), () => FailedAt);

        result.Should().BeNull();
        _store.Verify(s => s.Update(It.IsAny<RunRecord>()), Times.Never);
    }

    [Fact]
    public async Task QueueOrAbandonAsync_NeverPassesACancellableToken()
    {
        // A caller hanging up, or a tick cancelled at shutdown, must not be able to strand an admitted run.
        await AdmittedRunDispatch.QueueOrAbandonAsync(_queue.Object, _store.Object, Admitted(), () => FailedAt);

        _queue.Verify(q => q.EnqueueAsync("job-1", It.Is<CancellationToken>(t => !t.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task QueueOrAbandonAsync_QueueThrows_RecordsTheRunFailedAndReturnsTheException()
    {
        var boom = new InvalidOperationException("queue unavailable");
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        RunRecord? updated = null;
        _store.Setup(s => s.Update(It.IsAny<RunRecord>())).Callback<RunRecord>(r => updated = r).Returns(true);

        var result = await AdmittedRunDispatch.QueueOrAbandonAsync(_queue.Object, _store.Object, Admitted(), () => FailedAt);

        // Terminal, so the sweeper reclaims it and its owner's slot and target are released.
        result.Should().BeSameAs(boom);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(RunStatus.Failed);
        updated.Error.Should().Be(AdmittedRunDispatch.NotQueuedError);
        updated.CompletedAt.Should().Be(FailedAt, "each caller chooses its own failure timestamp");
        updated.JobId.Should().Be("job-1");
    }
}
