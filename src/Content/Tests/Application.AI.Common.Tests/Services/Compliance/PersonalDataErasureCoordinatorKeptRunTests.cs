using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// The run records the run store kept, on the receipt (#926). The store erases the person's finished runs and keeps every
/// run that has not ended, so a receipt that read <c>Full</c> while one was kept would tell the person their data is gone
/// while a record naming them is still held.
/// </summary>
public sealed class PersonalDataErasureCoordinatorKeptRunTests
{
    private const string Owner = "Owner-1";
    private const string KeptLine = "workflow and evaluation runs still held";

    private readonly Mock<IErasureOrchestrator> _graph = new();
    private readonly Mock<IKeptRunSource> _keptRuns = new();
    private readonly Mock<IOwnedRunCanceller> _liveRuns = new();
    private readonly Mock<IPlannedTaskCanceller> _runningTasks = new();
    private readonly List<string> _calls = [];

    public PersonalDataErasureCoordinatorKeptRunTests()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErasureReceipt
            {
                RequestId = "r",
                ScopeId = Owner,
                RequestedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch,
                NodesDeleted = 0,
                EdgesDeleted = 0,
                FeedbackWeightsDeleted = 0,
                VectorEmbeddingsDeleted = 0,
            });
    }

    private IPersonalDataEraser Eraser(string name, ErasureStage stage)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add(name))
            .ReturnsAsync(1);
        return eraser.Object;
    }

    private Task<ErasureReceipt> EraseAsync() =>
        new PersonalDataErasureCoordinator(
                _graph.Object,
                [Eraser("workflow and evaluation runs", ErasureStage.BeforeConversations), Eraser("plans", ErasureStage.PlannedTasks)],
                [],
                new AmbientRequestScope(),
                TimeProvider.System,
                NullLogger<PersonalDataErasureCoordinator>.Instance,
                runningTasks: _runningTasks.Object,
                keptRuns: _keptRuns.Object,
                liveRuns: _liveRuns.Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

    [Fact]
    public async Task EraseOwnerAsync_ARunRecordStillHeld_ListsItAndTheReceiptIsPartial()
    {
        _keptRuns.Setup(s => s.CountRunsKeptAsync(Owner, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("count"))
            .ReturnsAsync(2);

        var receipt = await EraseAsync();

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().ContainSingle(s => s.Store == KeptLine)
            .Which.Status.Should().Be(StoreErasureStatus.Skipped);
        receipt.CompletenessReason.Should().Contain("run erasure again");
        _calls.Should().ContainInOrder(["workflow and evaluation runs", "plans", "count"],
            "the count is of what the run store kept, so it is read after the store was erased");
    }

    [Fact]
    public async Task EraseOwnerAsync_ARunRecordStillHeld_HoldsNoStageBack()
    {
        // Nothing else is found through a run record, so a kept run must not keep the person's plans or conversations.
        _keptRuns.Setup(s => s.CountRunsKeptAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var receipt = await EraseAsync();

        _calls.Should().Contain("plans");
        receipt.Stores.Single(s => s.Store == "plans").Status.Should().Be(StoreErasureStatus.Erased);
    }

    [Fact]
    public async Task EraseOwnerAsync_NoRunRecordStillHeld_AddsNoLineAndTheReceiptIsFull()
    {
        _keptRuns.Setup(s => s.CountRunsKeptAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var receipt = await EraseAsync();

        receipt.Stores.Should().NotContain(s => s.Store == KeptLine);
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task EraseOwnerAsync_KeptRunsCannotBeCounted_ListsAFailureWithoutTheExceptionText()
    {
        _keptRuns.Setup(s => s.CountRunsKeptAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret connection string"));

        var receipt = await EraseAsync();

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial,
            "whether a record naming the person was kept is unknown, so the receipt cannot say they are gone");
        var line = receipt.Stores.Should().ContainSingle(s => s.Store == KeptLine).Subject;
        line.Status.Should().Be(StoreErasureStatus.Failed);
        line.Reason.Should().NotContain("secret");
    }

    private void RecordRunSteps()
    {
        _liveRuns.Setup(c => c.CancelRunsAsync(Owner)).Callback(() => _calls.Add("cancel runs")).ReturnsAsync(0);
        _liveRuns.Setup(c => c.WaitForRunsAsync(Owner, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("wait for runs"))
            .ReturnsAsync(0);
        _runningTasks.Setup(c => c.StopRunningTasksAsync(Owner, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("stop planned tasks"))
            .ReturnsAsync(0);
    }

    [Fact]
    public async Task EraseOwnerAsync_LiveRuns_AreEndedBeforeAnyStoreIsErased()
    {
        // #992. The run store erases only finished runs, so a queued or parked run cancelled after it would survive the
        // request and keep the receipt Partial on every retry.
        RecordRunSteps();

        await EraseAsync();

        _calls.Should().ContainInOrder(["cancel runs", "wait for runs", "workflow and evaluation runs"]);
    }

    [Fact]
    public async Task EraseOwnerAsync_LiveRuns_AreCancelledBeforePlannedTasksAreStopped()
    {
        // A queued workflow run claimed after the planned tasks were looked for would start one nothing stops.
        RecordRunSteps();

        await EraseAsync();

        _calls.Should().ContainInOrder(["cancel runs", "stop planned tasks"]);
    }

    [Fact]
    public async Task EraseOwnerAsync_WaitForRuns_RunsAlongsideThePlannedTaskStop()
    {
        // Each waits up to its own bound; one after the other, a workflow that ignored its signal would hold the request
        // for both. The stop here completes only once the wait has started, so run in turn it times out first.
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapped = false;
        _liveRuns.Setup(c => c.CancelRunsAsync(Owner)).ReturnsAsync(0);
        _liveRuns.Setup(c => c.WaitForRunsAsync(Owner, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                waitStarted.TrySetResult();
                return Task.FromResult(0);
            });
        _runningTasks.Setup(c => c.StopRunningTasksAsync(Owner, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                overlapped = true;
                return 0;
            });

        await EraseAsync();

        overlapped.Should().BeTrue("the wait for runs must start while the planned-task stop is still waiting");
    }

    [Fact]
    public async Task EraseOwnerAsync_CancellingRunsThrows_StillErasesEveryStore()
    {
        // A fault here must not shield the rest of the person's data; what it failed to stop is still counted as kept.
        _liveRuns.Setup(c => c.CancelRunsAsync(Owner)).ThrowsAsync(new InvalidOperationException("secret connection string"));
        _liveRuns.Setup(c => c.WaitForRunsAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret connection string"));
        _keptRuns.Setup(s => s.CountRunsKeptAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var receipt = await EraseAsync();

        _calls.Should().Contain(["workflow and evaluation runs", "plans"]);
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.CompletenessReason.Should().NotContain("secret");
    }
}
