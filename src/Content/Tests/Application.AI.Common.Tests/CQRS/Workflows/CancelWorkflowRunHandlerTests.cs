using Application.AI.Common.CQRS.Workflows.CancelRun;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Workflows;

/// <summary>
/// Tests for <see cref="CancelWorkflowRunCommandHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two things are load-bearing here and they are different in kind. The first is the same rule every
/// run endpoint follows — a caller may only act on its own run, and a refusal must not reveal whether
/// anyone else's exists. Cancelling raises the stakes: an endpoint that leaked existence here would
/// let one caller stop another's work.
/// </para>
/// <para>
/// The second is that cancelling a run has to take its pending approval with it. An approval request
/// that outlives its run sits in a person's queue with nothing left to decide, and answering it
/// affects a later run of the same workflow — which resumes the same persisted plan.
/// </para>
/// </remarks>
public sealed class CancelWorkflowRunHandlerTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly Mock<IRunJobStore> _runStore = new();
    private readonly Mock<IRunProgressBroker> _progress = new();
    private readonly Mock<IPlanRunCancellationRegistry> _registry = new();
    private readonly Mock<IEscalationService> _escalations = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();
    private readonly Guid _workflowId = Guid.NewGuid();

    /// <summary>Records the order in which the handler touched each collaborator.</summary>
    private readonly List<string> _calls = [];

    private readonly RecordingLogger _logger = new();

    /// <summary>
    /// Captures the severity of what the handler logged.
    /// </summary>
    /// <remarks>
    /// Severity is behaviour here, not decoration. An approval that resolved between a run parking and
    /// being cancelled is a benign race that resolves itself; logging it as an error puts a page in
    /// front of an operator with nothing to fix, and does so on the exact path that races most.
    /// </remarks>
    private sealed class RecordingLogger : ILogger<CancelWorkflowRunCommandHandler>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private CancelWorkflowRunCommandHandler BuildSut()
    {
        _config.AI.WorkflowSubmission.Enabled = true;

        return new CancelWorkflowRunCommandHandler(
            _runStore.Object, _progress.Object, _registry.Object, _escalations.Object,
            new StaticOptionsMonitor<AppConfig>(_config), _time, _logger);
    }

    private CancelWorkflowRunCommand Command(string jobId = "job-1", string ownerId = "alice") => new()
    {
        WorkflowId = _workflowId,
        JobId = jobId,
        OwnerId = ownerId,
        TenantId = "acme"
    };

    private RunRecord Run(RunStatus status, params Guid[] awaiting) => new()
    {
        JobId = "job-1",
        Kind = RunKind.Workflow,
        TargetId = _workflowId.ToString(),
        OwnerId = "alice",
        TenantId = "acme",
        Envelope = new CapabilityEnvelope(),
        Status = status,
        CreatedAt = _time.GetUtcNow(),
        AwaitingEscalationIds = awaiting
    };

    /// <summary>The run the store reports to a scoped read, and what the store does when told to cancel.</summary>
    private void StoreHolds(RunRecord? visible, RunRecord? cancelReturns)
    {
        _runStore
            .Setup(s => s.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(visible);

        _runStore
            .Setup(s => s.TryCancel(It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .Callback(() => _calls.Add("cancel-run"))
            .Returns(cancelReturns);

        _escalations
            .Setup(e => e.CancelEscalationAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("withdraw-approval"))
            .ReturnsAsync((Guid id, string _, string _, CancellationToken _) => new EscalationOutcome
            {
                EscalationId = id,
                IsApproved = false,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UnixEpoch,
                Approvers = ["carol"]
            });
    }

    [Fact]
    public async Task CancellingAParkedRun_WithdrawsTheApprovalItWasWaitingOn()
    {
        // The decision this wave settled. A gate left pending after its run is gone asks a person to
        // decide something that cannot happen — and because a workflow's plan state outlives any one
        // run, a verdict given for a dead run would be reconciled by the next one.
        var escalation = Guid.NewGuid();
        var parked = Run(RunStatus.Blocked, escalation);
        StoreHolds(visible: parked, cancelReturns: parked);

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StoppedImmediately.Should().BeTrue();
        result.Value.WithdrawnApprovals.Should().Be(1);

        _escalations.Verify(e => e.CancelEscalationAsync(
            escalation, It.IsAny<string>(), "alice", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheRunIsStoppedBeforeItsApprovalIsWithdrawn()
    {
        // Order is a correctness property, not a style choice. Withdrawing first resolves the
        // escalation while the run is still parked on it — which is precisely what the resume check
        // watches for, so the cancellation would put the run back to work on its way to stopping it.
        var parked = Run(RunStatus.Blocked, Guid.NewGuid());
        StoreHolds(visible: parked, cancelReturns: parked);

        await BuildSut().Handle(Command(), CancellationToken.None);

        _calls.Should().Equal("cancel-run", "withdraw-approval");
    }

    [Fact]
    public async Task CancellingAQueuedRun_TellsAnyoneWatchingThatItEnded()
    {
        // No dispatch will ever run for this job, and the dispatcher is what normally publishes the
        // terminal event. Without one here, a client streaming a queued run holds its connection and a
        // stream slot waiting for an end that nothing will announce.
        var queued = Run(RunStatus.Queued);
        StoreHolds(visible: queued, cancelReturns: queued);

        await BuildSut().Handle(Command(), CancellationToken.None);

        _progress.Verify(p => p.Publish(
            "job-1", RunProgressKind.RunFinished, null, null, nameof(RunStatus.Cancelled), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task CancellingARunningRun_SignalsItRatherThanClaimingItStopped()
    {
        // Work in flight can only be asked to stop. Reporting it as stopped would have the caller
        // start a replacement immediately — which is refused, because the first run still holds the
        // workflow, so the caller would be told its own cancellation had failed.
        StoreHolds(visible: Run(RunStatus.Running), cancelReturns: null);

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StoppedImmediately.Should().BeFalse();
        _registry.Verify(r => r.TryCancel(new PlanId(_workflowId)), Times.Once);
    }

    [Fact]
    public async Task CancellingAFinishedRun_IsAConflictAndStopsNothing()
    {
        // There is nothing to cancel, and answering success would suggest this call changed something.
        StoreHolds(visible: Run(RunStatus.Succeeded), cancelReturns: null);

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Conflict);
        _runStore.Verify(s => s.TryCancel(It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _registry.Verify(r => r.TryCancel(It.IsAny<PlanId>()), Times.Never);
    }

    [Fact]
    public async Task ARunTheCallerCannotSee_IsNotFoundAndIsNotCancelled()
    {
        // The store answers as though another owner's run does not exist. Distinguishing the two here
        // would hand one caller a way to stop another's work by guessing identifiers.
        StoreHolds(visible: null, cancelReturns: null);

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _runStore.Verify(s => s.TryCancel(It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _registry.Verify(r => r.TryCancel(It.IsAny<PlanId>()), Times.Never);
    }

    [Fact]
    public async Task AJobReachedThroughTheWrongWorkflowsRoute_IsNotFound()
    {
        // The route asserts a relationship. Honouring a request that contradicts it would confirm which
        // workflow a job belongs to by trying routes until one worked.
        StoreHolds(visible: Run(RunStatus.Blocked) with { TargetId = Guid.NewGuid().ToString() },
            cancelReturns: null);

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _runStore.Verify(s => s.TryCancel(It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task AnApprovalThatCannotBeWithdrawn_DoesNotFailTheCancellation()
    {
        // The run is already stopped by this point. Reporting failure would invite a retry of a cancel
        // with nothing left to do, and the caller cannot fix what actually went wrong.
        var parked = Run(RunStatus.Blocked, Guid.NewGuid(), Guid.NewGuid());
        StoreHolds(visible: parked, cancelReturns: parked);

        var attempts = 0;
        _escalations
            .Setup(e => e.CancelEscalationAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref attempts) == 1
                ? throw new TimeoutException("the escalation store is down")
                : Task.FromResult(new EscalationOutcome
                {
                    EscalationId = Guid.NewGuid(),
                    IsApproved = false,
                    Decisions = [],
                    ResolutionType = EscalationResolutionType.Denied,
                    ResolvedAt = DateTimeOffset.UnixEpoch,
                    Approvers = ["carol"]
                }));

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StoppedImmediately.Should().BeTrue();
        result.Value.WithdrawnApprovals.Should().Be(1,
            "the count is what was actually withdrawn, not what was attempted — one approval is still "
            + "sitting in someone's queue and the caller should be able to tell");
        attempts.Should().Be(2, "one failure must not abandon the approvals after it");
    }

    [Fact]
    public async Task AnApprovalAlreadyDecided_IsNotTreatedAsAFailure()
    {
        // The ordinary race, not a fault: a gate can time out or be answered between a run parking on
        // it and anyone cancelling that run. The escalation service reports that by throwing, and it is
        // the same exception a genuinely broken store would surface — so the two are distinguished by
        // its documented contract rather than by hoping they differ.
        var parked = Run(RunStatus.Blocked, Guid.NewGuid());
        StoreHolds(visible: parked, cancelReturns: parked);

        _escalations
            .Setup(e => e.CancelEscalationAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No pending escalation found with ID ..."));

        var result = await BuildSut().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StoppedImmediately.Should().BeTrue();
        result.Value.WithdrawnApprovals.Should().Be(0,
            "nothing was withdrawn because there was nothing left pending to withdraw");

        _logger.Levels.Should().NotContain(LogLevel.Error,
            "a race that resolved itself correctly must not page an operator who has nothing to fix");
    }

    [Fact]
    public async Task AnApprovalLeftPendingByAFailure_IsLoggedAsAProblem()
    {
        // The other side of the same judgement. This one really did leave a request in somebody's
        // queue with nothing left to decide, and nothing else will ever notice — the caller has been
        // told its run stopped, which is true.
        var parked = Run(RunStatus.Blocked, Guid.NewGuid());
        StoreHolds(visible: parked, cancelReturns: parked);

        _escalations
            .Setup(e => e.CancelEscalationAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the escalation store is down"));

        await BuildSut().Handle(Command(), CancellationToken.None);

        _logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Fact]
    public async Task WhenWorkflowSubmissionIsDisabled_NothingIsCancelled()
    {
        // The feature gate is the whole surface, not just its entry point. Leaving cancel reachable
        // while the rest is off would let a caller act on runs the host has stopped offering.
        StoreHolds(visible: Run(RunStatus.Blocked), cancelReturns: null);
        var sut = BuildSut();
        _config.AI.WorkflowSubmission.Enabled = false;

        var result = await sut.Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _runStore.Verify(s => s.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
