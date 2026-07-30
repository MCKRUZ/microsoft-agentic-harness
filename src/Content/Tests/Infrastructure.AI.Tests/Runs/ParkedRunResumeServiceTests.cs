using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Escalation;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="ParkedRunResumeService"/>.
/// </summary>
/// <remarks>
/// <para>
/// This service is the trigger that makes a human gate more than a place work stops. Everything on
/// either side of it already worked: a gate queues an escalation and parks its step, and the plan
/// executor reconciles blocked steps against their verdicts whenever it next runs. Nothing asked it to
/// run again — so an approved gate released nothing, and the run waited out the parked-run ceiling and
/// failed, days after the approver said yes.
/// </para>
/// <para>
/// That is the defect shape these tests exist for: every part present and correct, and no caller. It
/// does not show up in a unit test of any one part, because each part passes.
/// </para>
/// </remarks>
public sealed class ParkedRunResumeServiceTests
{
    /// <summary>
    /// Longest a test waits for a pass to happen. The service's interval floor is one second, so a
    /// pass costs about that; this is generous enough to survive a loaded agent and short enough that
    /// a genuinely stuck service fails the test rather than hanging the suite.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();
    private readonly Mock<IEscalationService> _escalations = new();

    private IRunJobStore _store = null!;
    private RecordingQueue _queue = null!;

    public ParkedRunResumeServiceTests()
    {
        // Below the service's own floor, which clamps it to one second — the shortest a real pass can
        // be scheduled, and short enough for a test to observe one.
        _config.AI.WorkflowSubmission.ParkedRunResumeInterval = TimeSpan.Zero;
    }

    /// <summary>Records what the service asked the dispatcher to run, and can refuse to accept it.</summary>
    private sealed class RecordingQueue : IRunDispatchQueue
    {
        private readonly ConcurrentQueue<string> _enqueued = new();

        /// <summary>When set, every enqueue throws this — standing in for a closed channel.</summary>
        public Exception? FailWith { get; set; }

        public IReadOnlyList<string> Enqueued => [.. _enqueued];

        public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
                throw FailWith;

            _enqueued.Enqueue(jobId);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Nothing drains the queue in these tests.");
    }

    [Fact]
    public async Task AnAnsweredGate_PutsTheRunBackInTheQueueUnderTheSameJobId()
    {
        // The whole point of the wave, and of the same-job-id decision: a caller submits once, polls
        // one id, and gets an answer — the approval is an event inside that run, not a new one.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);
        Answered(escalation, approved: true);

        await RunUntilAsync(() => _queue.Enqueued.Count > 0);

        _queue.Enqueued.Should().Equal("job-1");
        _store.Get("job-1", "alice", null)!.Status.Should().Be(
            RunStatus.Queued, "only a queued run is claimable, so anything else enqueues an id nothing acts on");
    }

    [Fact]
    public async Task ADeniedGate_ResumesTheRunToo()
    {
        // Resuming is not "continue the work" — it is "let the plan act on the answer". A denial has to
        // re-enter execution for the gate to fail and the run to reach a terminal state. Resuming only
        // on approval would leave every rejected workflow parked until the ceiling failed it, which
        // reports a decision that was made as a decision that never came.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);
        Answered(escalation, approved: false);

        await RunUntilAsync(() => _queue.Enqueued.Count > 0);

        _queue.Enqueued.Should().Equal("job-1");
    }

    [Fact]
    public async Task AGateNobodyHasAnsweredYet_LeavesTheRunParked()
    {
        // The other half of the contract. Resuming on an open gate would re-run the plan, which would
        // re-park immediately — a busy loop against the LLM and tool budget for as long as the approver
        // takes to answer.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);
        StillOpen(escalation);

        await RunForAtLeastOnePassAsync();

        _queue.Enqueued.Should().BeEmpty();
        _store.Get("job-1", "alice", null)!.Status.Should().Be(RunStatus.Blocked);
    }

    [Fact]
    public async Task OneOfTwoGatesAnswered_IsEnoughToResume()
    {
        // A plan can reach two gates on parallel branches. Waiting for both would stall on whichever
        // approver is slower even when the other's verdict is enough to release work — and if the
        // second gate is still open, reconciliation simply parks the run again on that one alone.
        var answered = Guid.NewGuid();
        var open = Guid.NewGuid();
        ParkRun("job-1", answered, open);
        Answered(answered, approved: true);
        StillOpen(open);

        await RunUntilAsync(() => _queue.Enqueued.Count > 0);

        _queue.Enqueued.Should().Equal("job-1");
    }

    [Fact]
    public async Task ARunIsResumedOnlyOnce_HoweverManyPassesRun()
    {
        // The verdict does not go away once read, so every later pass would see the same answered
        // escalation. Enqueuing again would run the plan a second time concurrently with the first —
        // two schedulers writing one plan's step states, and the caller billed twice.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);
        Answered(escalation, approved: true);

        await RunUntilAsync(() => _queue.Enqueued.Count > 0, thenKeepRunningFor: TimeSpan.FromSeconds(3));

        _queue.Enqueued.Should().Equal("job-1");
    }

    [Fact]
    public async Task AnEnqueueThatFails_LeavesTheRunParkedRatherThanStranded()
    {
        // The one state nothing can recover from: Queued but not in the queue. No dispatcher claims a
        // run it was never handed, and the parked-run ceiling only looks at parked runs — so the run
        // would sit untouched and unreadable-as-finished for the life of the process. Putting it back
        // costs one retried pass.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);
        Answered(escalation, approved: true);
        _queue.FailWith = new InvalidOperationException("the queue is closed");

        await RunForAtLeastOnePassAsync();

        var run = _store.Get("job-1", "alice", null)!;
        run.Status.Should().Be(RunStatus.Blocked);
        run.ParkedAt.Should().NotBeNull("the ceiling can only give up on a run whose wait it can measure");
        run.AwaitingEscalationIds.Should().Equal([escalation], "the next pass has to know what to ask about");
    }

    [Fact]
    public async Task AFailedPass_DoesNotStopTheService()
    {
        // A background loop that dies on one bad pass takes every parked run in the host with it, and
        // does so silently — the symptom is gates that stop releasing, not an error anyone sees.
        var escalation = Guid.NewGuid();
        ParkRun("job-1", escalation);

        var asked = 0;
        _escalations
            .Setup(e => e.GetOutcomeAsync(escalation, It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref asked) == 1
                ? throw new InvalidOperationException("the escalation store is down")
                : Task.FromResult<EscalationOutcome?>(Outcome(escalation, approved: true)));

        await RunUntilAsync(() => _queue.Enqueued.Count > 0);

        _queue.Enqueued.Should().Equal("job-1");
    }

    private void ParkRun(string jobId, params Guid[] awaiting)
    {
        _store = new InMemoryRunJobStore(new Support.StaticOptionsMonitor<AppConfig>(_config), _time);
        _queue = new RecordingQueue();

        var record = new RunRecord
        {
            JobId = jobId,
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = "alice",
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow()
        };

        _store.TryCreate(record, int.MaxValue).Should().Be(RunAdmission.Accepted);

        // Parked the way the dispatcher parks it, rather than by writing the status directly, so these
        // tests break if the dispatcher's park shape and the resume check's expectations diverge.
        var claimed = _store.TryBeginRun(jobId, _time.GetUtcNow())!;
        _store.Update(claimed with
        {
            Status = RunStatus.Blocked,
            ParkedAt = _time.GetUtcNow(),
            AwaitingEscalationIds = awaiting
        });
    }

    private void Answered(Guid escalationId, bool approved) =>
        _escalations
            .Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(escalationId, approved));

    private void StillOpen(Guid escalationId) =>
        _escalations
            .Setup(e => e.GetOutcomeAsync(escalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationOutcome?)null);

    private static EscalationOutcome Outcome(Guid escalationId, bool approved) => new()
    {
        EscalationId = escalationId,
        IsApproved = approved,
        Decisions = [],
        ResolutionType = approved
            ? EscalationResolutionType.Approved
            : EscalationResolutionType.Denied,
        ResolvedAt = DateTimeOffset.UnixEpoch,
        Approvers = ["carol"]
    };

    private ParkedRunResumeService BuildSut() =>
        new(_store, _queue, _escalations.Object,
            new Support.StaticOptionsMonitor<AppConfig>(_config),
            NullLogger<ParkedRunResumeService>.Instance);

    /// <summary>Runs the service until <paramref name="reached"/> holds, or the budget expires.</summary>
    private async Task RunUntilAsync(Func<bool> reached, TimeSpan? thenKeepRunningFor = null)
    {
        var sut = BuildSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        try
        {
            var deadline = DateTime.UtcNow + Budget;
            while (!reached() && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            reached().Should().BeTrue("the service had {0} to act on an answered decision", Budget);

            if (thenKeepRunningFor is { } extra)
                await Task.Delay(extra);
        }
        finally
        {
            await cts.CancelAsync();
            await sut.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs the service long enough that at least one pass has certainly happened, for the tests whose
    /// claim is that nothing changed.
    /// </summary>
    private async Task RunForAtLeastOnePassAsync()
    {
        var sut = BuildSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        try
        {
            // Two intervals plus slack. A shorter wait would pass even against a service that never ran
            // a pass at all, which is precisely the failure these tests are looking for.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await cts.CancelAsync();
            await sut.StopAsync(CancellationToken.None);
        }
    }
}
