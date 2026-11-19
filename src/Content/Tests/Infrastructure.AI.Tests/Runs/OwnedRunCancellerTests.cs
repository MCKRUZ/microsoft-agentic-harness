using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Runs;
using Domain.AI.Bundles;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="OwnedRunCanceller"/>, over the real run store (#992).
/// </summary>
/// <remarks>
/// <para>
/// Erasure removes only finished runs, so a person's live runs have to be ended first or they outlive the erasure: a queued
/// run starts afterwards and writes new personal data, and a parked one holds every retry <c>Partial</c> while its
/// approvals sit in an approver's queue. These tests pin that a parked run is cancelled with its approvals withdrawn, that
/// a queued one never starts, and that nobody else's run is touched.
/// </para>
/// </remarks>
public sealed class OwnedRunCancellerTests
{
    private const string Subject = "Subject-Oid";
    private const string Bystander = "bystander-oid";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();
    private readonly Mock<IEscalationService> _escalations = new();
    private readonly Mock<IRunProgressBroker> _progress = new();
    private readonly Mock<IPlanRunCancellationRegistry> _registry = new();
    private readonly InMemoryRunJobStore _store;

    public OwnedRunCancellerTests()
    {
        _store = new InMemoryRunJobStore(
            new StaticOptionsMonitor<AppConfig>(_config), _time, [], NullLogger<InMemoryRunJobStore>.Instance);
    }

    private OwnedRunCanceller Canceller(TimeSpan wait) => new(
        _store, _progress.Object, _escalations.Object, _registry.Object, _time, NullLogger<OwnedRunCanceller>.Instance, wait);

    /// <summary>Both halves, as erasure calls them, with nothing executing in between.</summary>
    private async Task<int> CancelAndWaitAsync(TimeSpan wait)
    {
        var canceller = Canceller(wait);
        await canceller.CancelRunsAsync(Subject);
        return await canceller.WaitForRunsAsync(Subject, CancellationToken.None);
    }

    private RunRecord Admit(string jobId, string ownerId, RunStatus status = RunStatus.Queued, params Guid[] awaiting)
    {
        var record = new RunRecord
        {
            JobId = jobId,
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = ownerId,
            TenantId = "acme",
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow(),
        };
        _store.TryCreate(record, int.MaxValue).Should().Be(RunAdmission.Accepted);

        if (status == RunStatus.Running)
            return _store.TryBeginRun(jobId, _time.GetUtcNow())!;

        if (status == RunStatus.Blocked)
        {
            var parked = record with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow(), AwaitingEscalationIds = awaiting };
            _store.Update(parked).Should().BeTrue();
            return parked;
        }

        return record;
    }

    private RunStatus StatusOf(string jobId, string ownerId) => _store.Get(jobId, ownerId, "acme")!.Status;

    [Fact]
    public async Task CancelRunsAsync_ParkedRun_IsCancelledAndItsApprovalsWithdrawnAsTheSystem()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Admit("parked", Subject, RunStatus.Blocked, first, second);

        var cancelled = await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        cancelled.Should().Be(1);
        StatusOf("parked", Subject).Should().Be(RunStatus.Cancelled);
        foreach (var escalation in new[] { first, second })
        {
            _escalations.Verify(e => e.CancelEscalationAsync(
                escalation, It.IsAny<string>(), OwnedRunCanceller.WithdrawnBy, It.IsAny<CancellationToken>()), Times.Once,
                "an approval that outlives its run is answerable by someone who cannot know it is moot, and naming the "
                + "person in the retained escalation audit would record them in the act of erasing them");
        }
    }

    [Fact]
    public async Task CancelRunsAsync_QueuedRun_IsCancelledAndItsWatchersAreTold()
    {
        Admit("queued", Subject);

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        StatusOf("queued", Subject).Should().Be(RunStatus.Cancelled);
        _progress.Verify(p => p.Publish("queued", RunProgressKind.RunFinished, null, null,
            nameof(RunStatus.Cancelled), It.IsAny<string>()), Times.Once,
            "no dispatch will finish this run, so without the event a watcher's stream never ends");
    }

    [Fact]
    public async Task CancelRunsAsync_QueuedRun_IsNeverStartedByTheDispatcher()
    {
        // One dispatch at a time, so the bystander's run being executed proves the cancelled one was dequeued first.
        _config.AI.WorkflowSubmission.MaxConcurrentDispatchedRuns = 1;
        var executed = new List<string>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRunKindExecutor>(RunKind.Workflow, new RecordingExecutor(executed));
        services.AddSingleton(Mock.Of<IKnowledgeScopeWriter>());
        var queue = new InMemoryRunDispatchQueue();
        var dispatcher = new RunDispatchBackgroundService(
            queue, _store, _progress.Object, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<AppConfig>(_config), _time, NullLogger<RunDispatchBackgroundService>.Instance);
        Admit("queued", Subject);
        Admit("sentinel", Bystander);
        await queue.EnqueueAsync("queued", CancellationToken.None);
        await queue.EnqueueAsync("sentinel", CancellationToken.None);

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);
        using var cts = new CancellationTokenSource();
        await dispatcher.StartAsync(cts.Token);
        for (var attempt = 0; attempt < 200 && _store.Get("sentinel", Bystander, "acme")!.IsTerminal is false; attempt++)
            await Task.Delay(10);
        await cts.CancelAsync();
        await dispatcher.StopAsync(CancellationToken.None);

        StatusOf("sentinel", Bystander).Should().Be(RunStatus.Succeeded, "the dispatcher must have reached the queue");
        executed.Should().Equal(["sentinel"], "a run erasure cancelled must never start after the person is erased");
    }

    [Fact]
    public async Task CancelRunsAsync_AnotherPersonsRuns_AreLeftLive()
    {
        var escalation = Guid.NewGuid();
        Admit("mine", Subject);
        Admit("theirs-queued", Bystander);
        Admit("theirs-parked", Bystander, RunStatus.Blocked, escalation);

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        StatusOf("theirs-queued", Bystander).Should().Be(RunStatus.Queued);
        StatusOf("theirs-parked", Bystander).Should().Be(RunStatus.Blocked);
        _escalations.Verify(e => e.CancelEscalationAsync(
            escalation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelRunsAsync_RunRecordedInAnotherCasing_IsStillThePersons()
    {
        // A run a schedule started before #907 records its owner lower-cased; it is still the person's.
        Admit("lower", Subject.ToLowerInvariant());

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        StatusOf("lower", Subject.ToLowerInvariant()).Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task CancelRunsAsync_ExecutingWorkflow_IsSignalledThroughItsPlanAndLeftToItsDispatch()
    {
        // Its status belongs to the dispatch in flight, so it is signalled rather than rewritten, and counted as live.
        var running = Admit("running", Subject, RunStatus.Running);

        var stillLive = await CancelAndWaitAsync(TimeSpan.Zero);

        stillLive.Should().Be(1);
        StatusOf("running", Subject).Should().Be(RunStatus.Running);
        _registry.Verify(r => r.TryCancel(new PlanId(Guid.Parse(running.TargetId))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task WaitForRunsAsync_WorkflowClaimedAfterTheWaitBegan_IsSignalled()
    {
        // A run queued after the first pass is claimed by an idle dispatcher within milliseconds, so it is executing, not
        // queued, by the next poll; the planned-task canceller looked for runs before it existed.
        var running = Admit("running", Subject, RunStatus.Running);
        var waiting = Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None);
        var late = Admit("late", Subject, RunStatus.Running);

        _store.Update(running with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });
        await AdvanceUntilDoneAsync(waiting);

        _registry.Verify(r => r.TryCancel(new PlanId(Guid.Parse(late.TargetId))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task WaitForRunsAsync_RunClaimedDuringTheWait_IsWaitedForUntilItEnds()
    {
        // A run queued when the wait began and claimed before it could be cancelled is executing by the next poll. Erasure
        // must wait for it as well, or it could still be writing the person's data when the stores are cleared. Before
        // #997's review only the runs executing at the start were waited for, so this returned as soon as `running` ended.
        var running = Admit("running", Subject, RunStatus.Running);
        var waiting = Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None);
        var late = Admit("late", Subject, RunStatus.Running);

        _store.Update(running with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });
        for (var step = 0; step < 8; step++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }

        waiting.IsCompleted.Should().BeFalse("the run claimed during the wait is still executing");

        _store.Update(late with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });
        var stillLive = await AdvanceUntilDoneAsync(waiting);

        stillLive.Should().Be(0);
    }

    [Fact]
    public async Task WaitForRunsAsync_ExecutingRunEndsDuringTheWait_ReportsNothingStillLive()
    {
        var running = Admit("running", Subject, RunStatus.Running);
        var waiting = Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None);

        _store.Update(running with { Status = RunStatus.Cancelled, CompletedAt = _time.GetUtcNow() });
        var stillLive = await AdvanceUntilDoneAsync(waiting);

        stillLive.Should().Be(0);
    }

    [Fact]
    public async Task WaitForRunsAsync_ExecutingRunParksBeforeItStops_IsCancelledOnALaterPoll()
    {
        // The stop signal can land after the plan reached a gate: the run parks instead of stopping, and a single pass
        // would leave it parked, holding its approval, through the erasure.
        var escalation = Guid.NewGuid();
        var running = Admit("running", Subject, RunStatus.Running);
        var waiting = Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None);

        _store.Update(running with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow(), AwaitingEscalationIds = [escalation] });
        var stillLive = await AdvanceUntilDoneAsync(waiting);

        stillLive.Should().Be(0);
        StatusOf("running", Subject).Should().Be(RunStatus.Cancelled);
        _escalations.Verify(e => e.CancelEscalationAsync(
            escalation, It.IsAny<string>(), OwnedRunCanceller.WithdrawnBy, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForRunsAsync_RunParksAsItIsSignalled_IsCancelledBeforeTheWaitEnds()
    {
        // The run reaches a gate while the stop signal is being sent, so the poll's re-read finds it parked and nothing
        // executing. Before #997's correctness review the wait ended on that read, leaving the run parked, holding its
        // approval, and the receipt Partial, with time still left to cancel it.
        var escalation = Guid.NewGuid();
        var running = Admit("running", Subject, RunStatus.Running);
        _registry.Setup(r => r.TryCancel(new PlanId(Guid.Parse(running.TargetId)))).Callback(() =>
            _store.Update(running with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow(), AwaitingEscalationIds = [escalation] }));

        var stillLive = await AdvanceUntilDoneAsync(
            Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None));

        stillLive.Should().Be(0);
        StatusOf("running", Subject).Should().Be(RunStatus.Cancelled);
        _escalations.Verify(e => e.CancelEscalationAsync(
            escalation, It.IsAny<string>(), OwnedRunCanceller.WithdrawnBy, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForRunsAsync_RunQueuedAfterThePendingRunsWereCancelled_IsCancelledToo()
    {
        // A schedule or a client can queue a run between the two halves, while the planned tasks are being stopped.
        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);
        Admit("late", Subject);

        var stillLive = await Canceller(TimeSpan.Zero).WaitForRunsAsync(Subject, CancellationToken.None);

        stillLive.Should().Be(0);
        StatusOf("late", Subject).Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task WaitForRunsAsync_ExecutingRunNeverEnds_GivesUpAtTheBound()
    {
        Admit("running", Subject, RunStatus.Running);
        var started = _time.GetUtcNow();

        var stillLive = await AdvanceUntilDoneAsync(Canceller(TimeSpan.FromSeconds(5)).WaitForRunsAsync(Subject, CancellationToken.None));

        stillLive.Should().Be(1);
        (_time.GetUtcNow() - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5),
            "it waits out the bound before going on without a run that never ends");
    }

    [Fact]
    public async Task WaitForRunsAsync_NothingExecuting_ReturnsWithoutWaiting()
    {
        Admit("queued", Subject);

        var waiting = Canceller(TimeSpan.FromSeconds(30)).WaitForRunsAsync(Subject, CancellationToken.None);

        waiting.IsCompleted.Should().BeTrue("with nothing executing there is nothing to wait for");
        (await waiting).Should().Be(0);
    }

    [Fact]
    public async Task CancelRunsAsync_OneRunCannotBeStopped_TheRestAreStillCancelled()
    {
        // A fault on one run must not leave the person's other runs going on after the erasure.
        Admit("running", Subject, RunStatus.Running);
        Admit("queued", Subject);
        _registry.Setup(r => r.TryCancel(It.IsAny<PlanId>())).Throws(new InvalidOperationException("registry down"));

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        StatusOf("queued", Subject).Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task CancelRunsAsync_WatchersCannotBeTold_TheApprovalsAreStillWithdrawn()
    {
        // The run is terminal once cancelled, so nothing later comes back for its approvals; a broker fault must not skip
        // the withdrawal.
        var escalation = Guid.NewGuid();
        Admit("parked", Subject, RunStatus.Blocked, escalation);
        _progress.Setup(p => p.Publish("parked", It.IsAny<RunProgressKind>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Throws(new InvalidOperationException("broker down"));

        await Canceller(TimeSpan.Zero).CancelRunsAsync(Subject);

        StatusOf("parked", Subject).Should().Be(RunStatus.Cancelled);
        _escalations.Verify(e => e.CancelEscalationAsync(
            escalation, It.IsAny<string>(), OwnedRunCanceller.WithdrawnBy, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Advances the fake clock in poll-sized steps until <paramref name="pending"/> completes.</summary>
    private async Task<int> AdvanceUntilDoneAsync(Task<int> pending)
    {
        for (var step = 0; step < 1_000 && !pending.IsCompleted; step++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }

        return await pending;
    }

    private sealed class RecordingExecutor(List<string> executed) : IRunKindExecutor
    {
        public Task<Result<RunCompletion>> ExecuteAsync(RunRecord record, CancellationToken cancellationToken)
        {
            executed.Add(record.JobId);
            return Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded()));
        }
    }
}
