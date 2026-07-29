using Application.AI.Common.CQRS.Workflows.GetRun;
using Application.AI.Common.CQRS.Workflows.StartRun;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Workflows;

/// <summary>
/// Tests for the run start and status handlers.
/// </summary>
/// <remarks>
/// The load-bearing properties are that a caller cannot start or read a run against a workflow it
/// does not own, and that neither refusal reveals whether the thing exists — a job identifier is the
/// only thing separating callers, so an answer that distinguishes "not yours" from "not there" turns
/// the endpoint into a way to enumerate other people's work.
/// </remarks>
public sealed class WorkflowRunHandlerTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly Mock<IPlanStateStore> _planStore = new();
    private readonly Mock<IRunJobStore> _runStore = new();
    private readonly Mock<IRunDispatchQueue> _queue = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private StartWorkflowRunCommandHandler BuildStartSut(
        bool ownsWorkflow = true,
        int maxConcurrent = 10,
        RunAdmission admission = RunAdmission.Accepted)
    {
        _config.AI.WorkflowSubmission.Enabled = true;
        _config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner = maxConcurrent;

        _planStore.Setup(s => s.IsPlanWritableByCallerAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(ownsWorkflow));

        _runStore.Setup(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>())).Returns(admission);

        _queue.Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        return new StartWorkflowRunCommandHandler(
            _planStore.Object, _runStore.Object, _queue.Object,
            new StaticOptionsMonitor<AppConfig>(_config), _time,
            NullLogger<StartWorkflowRunCommandHandler>.Instance);
    }

    private GetWorkflowRunQueryHandler BuildGetSut()
    {
        _config.AI.WorkflowSubmission.Enabled = true;
        return new GetWorkflowRunQueryHandler(_runStore.Object, new StaticOptionsMonitor<AppConfig>(_config));
    }

    private static StartWorkflowRunCommand StartCommand(Guid workflowId, string ownerId = "alice") => new()
    {
        WorkflowId = workflowId,
        OwnerId = ownerId,
        TenantId = "acme",
        Envelope = new CapabilityEnvelope()
    };

    [Fact]
    public async Task Start_WorkflowTheCallerDoesNotOwn_IsNotFoundAndQueuesNothing()
    {
        var sut = BuildStartSut(ownsWorkflow: false);

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound,
            "not-yours and not-there must be the same answer, or the endpoint enumerates other callers' work");

        _runStore.Verify(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_OwnedWorkflow_RecordsTheRunBeforeQueueingIt()
    {
        // Order matters: queued first, a dispatcher could claim a job the store has never heard of and
        // the run would vanish while the caller holds an identifier that never resolves.
        var sut = BuildStartSut();
        var sequence = new List<string>();

        _runStore.Setup(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>()))
            .Callback(() => sequence.Add("create"))
            .Returns(RunAdmission.Accepted);

        _queue.Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("enqueue"))
            .Returns(ValueTask.CompletedTask);

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.JobId.Should().NotBeNullOrWhiteSpace();
        sequence.Should().Equal("create", "enqueue");
    }

    [Fact]
    public async Task Start_RecordsTheCallerAndTheirEnvelopeOnTheRun()
    {
        // The run outlives the request and executes with no caller attached, so the identity that
        // authorized it has to travel on the record itself.
        var sut = BuildStartSut();
        RunRecord? created = null;
        _runStore.Setup(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>()))
            .Callback<RunRecord, int>((record, _) => created = record)
            .Returns(RunAdmission.Accepted);

        var workflowId = Guid.NewGuid();
        await sut.Handle(StartCommand(workflowId), CancellationToken.None);

        created.Should().NotBeNull();
        created!.OwnerId.Should().Be("alice");
        created.TenantId.Should().Be("acme");
        created.TargetId.Should().Be(workflowId.ToString());
        created.Kind.Should().Be(RunKind.Workflow);
        created.Status.Should().Be(RunStatus.Queued);
    }

    [Fact]
    public async Task Start_PassesTheConfiguredCapToTheStoreRatherThanCheckingItHere()
    {
        // The cap has to be applied where it can be applied atomically. Deciding it here and inserting
        // afterwards leaves a window in which concurrent requests all see room and all proceed.
        var sut = BuildStartSut(maxConcurrent: 4);

        await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        _runStore.Verify(s => s.TryCreate(It.IsAny<RunRecord>(), 4), Times.Once);
    }

    [Fact]
    public async Task Start_WhenTheCallerIsAtItsConcurrencyCap_IsRefused()
    {
        var sut = BuildStartSut(maxConcurrent: 2, admission: RunAdmission.OwnerAtCapacity);

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*in flight*");
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_WhenTheWorkflowIsAlreadyRunning_IsAConflict()
    {
        // Distinct from being at capacity, and distinct from a validation error: the caller is not at
        // fault and nothing about the request is malformed. It is a state that clears on its own, and
        // 409 is what tells a caller to retry rather than to change what it asked for.
        var sut = BuildStartSut(admission: RunAdmission.TargetAlreadyRunning);

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Conflict);
        result.Errors.Should().ContainMatch("*already has a run in progress*");
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_DoesNotQueueOnTheCallersToken()
    {
        // Past admission the record is committed. A record that is committed but never queued is never
        // claimed, never finishes, and — because only terminal runs are reclaimed — never goes away:
        // it pins its workflow at 409 and holds one of the owner's slots for the life of the process.
        // Hanging up mid-request is ordinary client behaviour and must not be able to do that.
        var sut = BuildStartSut();
        CancellationToken observed = default;
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, token) => observed = token)
            .Returns(ValueTask.CompletedTask);

        using var cts = new CancellationTokenSource();
        await sut.Handle(StartCommand(Guid.NewGuid()), cts.Token);

        observed.CanBeCanceled.Should().BeFalse(
            "the caller's token must not be able to abandon a run the caller was already told was accepted");
    }

    [Fact]
    public async Task Start_WhenQueueingFails_TheRunIsRecordedTerminalRatherThanLeftQueued()
    {
        // Unreachable with an in-process channel, but the queue is the seam for a durable one. The cost
        // of an unguarded failure is not a lost run: it is a workflow no caller can ever start again.
        var sut = BuildStartSut();
        RunRecord? created = null;
        RunRecord? updated = null;

        _runStore.Setup(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>()))
            .Callback<RunRecord, int>((record, _) => created = record)
            .Returns(RunAdmission.Accepted);

        _runStore.Setup(s => s.Update(It.IsAny<RunRecord>()))
            .Callback<RunRecord>(record => updated = record)
            .Returns(true);

        _queue.Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("the queue is gone"));

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        updated.Should().NotBeNull("a committed record that cannot be queued must be released, not stranded");
        updated!.JobId.Should().Be(created!.JobId);
        updated.IsTerminal.Should().BeTrue();
        updated.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Start_WhenSubmissionIsDisabled_IsRefused()
    {
        var sut = BuildStartSut();
        _config.AI.WorkflowSubmission.Enabled = false;

        var result = await sut.Handle(StartCommand(Guid.NewGuid()), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _runStore.Verify(s => s.TryCreate(It.IsAny<RunRecord>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Get_RunBelongingToAnotherCaller_IsNotFound()
    {
        // The store answers null for another owner, and the handler must not soften that into
        // anything that distinguishes it from a job id that was never issued.
        var sut = BuildGetSut();
        _runStore.Setup(s => s.Get("job-1", "mallory", It.IsAny<string?>())).Returns((RunRecord?)null);

        var result = await sut.Handle(
            new GetWorkflowRunQuery { WorkflowId = Guid.NewGuid(), JobId = "job-1", OwnerId = "mallory" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Get_RunBelongingToADifferentWorkflow_IsNotFound()
    {
        // The route asserts a relationship. Answering it with a record that contradicts the route
        // would let a caller discover which workflow a job belongs to by trying routes until one hit.
        var sut = BuildGetSut();
        var requested = Guid.NewGuid();

        _runStore.Setup(s => s.Get("job-1", "alice", It.IsAny<string?>())).Returns(new RunRecord
        {
            JobId = "job-1",
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = "alice",
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow()
        });

        var result = await sut.Handle(
            new GetWorkflowRunQuery { WorkflowId = requested, JobId = "job-1", OwnerId = "alice" },
            CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Get_TheCallersOwnRun_IsReturned()
    {
        var sut = BuildGetSut();
        var workflowId = Guid.NewGuid();

        _runStore.Setup(s => s.Get("job-1", "alice", It.IsAny<string?>())).Returns(new RunRecord
        {
            JobId = "job-1",
            Kind = RunKind.Workflow,
            TargetId = workflowId.ToString(),
            OwnerId = "alice",
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Running,
            CreatedAt = _time.GetUtcNow()
        });

        var result = await sut.Handle(
            new GetWorkflowRunQuery { WorkflowId = workflowId, JobId = "job-1", OwnerId = "alice" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(RunStatus.Running);
    }
}
