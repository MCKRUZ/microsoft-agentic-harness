using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="RunDispatchBackgroundService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The property that matters most is that a claimed run always reaches a terminal state. Once a run
/// is claimed it is <see cref="RunStatus.Running"/>, and only the dispatcher can move it out of that
/// state — so any path that escapes without recording an outcome leaves a caller polling a run
/// nothing will ever finish.
/// </para>
/// <para>
/// The second is that the run executes as the caller who started it. Scope is ambient and set at an
/// HTTP entry point; it does not survive the hop onto this thread. Unscoped is not a harmless default
/// in this codebase — an absent owner reads as a global record — so the dispatcher must either
/// establish the run's identity or refuse to run it.
/// </para>
/// </remarks>
public sealed class RunDispatchBackgroundServiceTests
{
    private sealed class StubExecutor(Func<RunRecord, Task<Result<RunCompletion>>> behaviour) : IRunKindExecutor
    {
        public int Invocations { get; private set; }

        public Task<Result<RunCompletion>> ExecuteAsync(RunRecord record, CancellationToken cancellationToken)
        {
            Invocations++;
            return behaviour(record);
        }
    }

    /// <summary>
    /// Records the scope armed around each job, and whether it was still armed when the executor ran.
    /// </summary>
    private sealed class RecordingScopeWriter : IKnowledgeScopeWriter
    {
        private sealed class Restore(RecordingScopeWriter owner) : IDisposable
        {
            public void Dispose() => owner.Current = null;
        }

        public (string? UserId, string? TenantId)? Current { get; private set; }

        public List<(string? UserId, string? TenantId)> Armed { get; } = [];

        public IDisposable SetScope(
            string? userId = null,
            string? tenantId = null,
            string? datasetId = null,
            string? datasetName = null,
            string? datasetOwnerId = null)
        {
            Current = (userId, tenantId);
            Armed.Add((userId, tenantId));
            return new Restore(this);
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();
    private readonly RecordingScopeWriter _scopeWriter = new();

    /// <summary>
    /// A real broker, so the dispatcher's terminal event is actually publishable. The dispatcher owns
    /// that event precisely because it is the one place every run reaches a terminal state.
    /// </summary>
    private InMemoryRunProgressBroker Progress => field ??= new InMemoryRunProgressBroker(
        new StaticOptionsMonitor<AppConfig>(_config), _time);

    private (RunDispatchBackgroundService Service, IRunJobStore Store, IRunDispatchQueue Queue) Build(
        IRunKindExecutor? executor, bool registerScopeWriter = true)
    {
        var services = new ServiceCollection();
        if (executor is not null)
            services.AddKeyedSingleton(RunKind.Workflow, executor);

        if (registerScopeWriter)
            services.AddSingleton<IKnowledgeScopeWriter>(_scopeWriter);

        var monitor = new StaticOptionsMonitor<AppConfig>(_config);
        var store = new InMemoryRunJobStore(monitor, _time);
        var queue = new InMemoryRunDispatchQueue();

        var service = new RunDispatchBackgroundService(
            queue, store, Progress,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            monitor, _time, NullLogger<RunDispatchBackgroundService>.Instance);

        return (service, store, queue);
    }

    private RunRecord Queued(string jobId = "job-1", string ownerId = "alice", string? tenantId = "acme") => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = ownerId,
        TenantId = tenantId,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };

    private static void Admit(IRunJobStore store, RunRecord record) =>
        store.TryCreate(record, int.MaxValue).Should().Be(RunAdmission.Accepted);

    /// <summary>Runs the dispatcher until <paramref name="jobId"/> is terminal, or the attempt budget is spent.</summary>
    private static Task<RunRecord?> DrainUntilTerminalAsync(
        RunDispatchBackgroundService service, IRunJobStore store, string jobId) =>
        DrainUntilAsync(service, store, jobId, r => r.IsTerminal);

    /// <summary>
    /// Runs the dispatcher until <paramref name="reached"/> holds for the run, or the attempts run out.
    /// </summary>
    /// <remarks>
    /// Predicated rather than fixed on terminality, because not every state the dispatcher drives a run
    /// into is terminal any more: a parked run has left <c>Running</c> and will never become terminal on
    /// its own, so waiting for terminality would time out on exactly the case under test.
    /// </remarks>
    private static async Task<RunRecord?> DrainUntilAsync(
        RunDispatchBackgroundService service,
        IRunJobStore store,
        string jobId,
        Func<RunRecord, bool> reached)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        RunRecord? record = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            record = store.Get(jobId, "alice", "acme");
            if (record is not null && reached(record))
                break;

            await Task.Delay(10);
        }

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
        return record;
    }

    [Fact]
    public async Task ASuccessfulRun_IsRecordedSucceeded()
    {
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded())));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Succeeded);
        record.CompletedAt.Should().NotBeNull();
        executor.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task TheRunsOwnerAndTenantAreArmedBeforeTheExecutorRuns()
    {
        // The whole point of carrying identity on the record. Without this the executor runs as nobody,
        // and in this codebase nobody reads as everybody — so the caller's own plan is invisible to its
        // own run while every global record is not.
        (string? UserId, string? TenantId)? seenByExecutor = null;
        var executor = new StubExecutor(_ =>
        {
            seenByExecutor = _scopeWriter.Current;
            return Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded()));
        });

        var (service, store, queue) = Build(executor);
        Admit(store, Queued(ownerId: "alice", tenantId: "acme"));
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        await DrainUntilTerminalAsync(service, store, "job-1");

        seenByExecutor.Should().Be(("alice", "acme"));
    }

    [Fact]
    public async Task TheRunsScopeIsReleasedBeforeTheNextJob()
    {
        // Disposed per job, not per loop. Left armed, job N's caller stays ambient for job N+1 and the
        // second run executes as the first user — a cross-owner read with no attacker required.
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded())));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        await DrainUntilTerminalAsync(service, store, "job-1");

        _scopeWriter.Armed.Should().ContainSingle();
        _scopeWriter.Current.Should().BeNull("the scope must not outlive the job it was armed for");
    }

    [Fact]
    public async Task AHostThatCannotEstablishIdentity_FailsTheRunRatherThanRunningItUnscoped()
    {
        // Running unscoped is not a degraded mode. An absent owner is read as a global record, so the
        // work would silently see and touch every caller's data.
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded())));
        var (service, store, queue) = Build(executor, registerScopeWriter: false);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Failed);
        record.Error.Should().Contain("identity");
        executor.Invocations.Should().Be(0, "the work must not run at all");
    }

    [Fact]
    public async Task AFailedRun_KeepsTheExecutorsReason()
    {
        // The executor's messages are caller-safe by contract, so they reach the caller rather than
        // being flattened into a uniform "it failed" that says nothing actionable.
        var executor = new StubExecutor(_ =>
            Task.FromResult(Result<RunCompletion>.Fail("the model deployment was rejected")));

        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Failed);
        record.Error.Should().Contain("the model deployment was rejected");
    }

    [Theory]
    [InlineData(RunStatus.Cancelled)]
    public async Task AnOutcomeThatIsNeitherSuccessNorFailure_IsRecordedAsItself(RunStatus outcome)
    {
        // Work can end in more ways than worked and broke. Collapsing those onto a boolean is what
        // makes a workflow parked awaiting an approval report to its caller as finished work.
        var completion = new RunCompletion { Status = outcome, Detail = "stopped on request" };
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(completion)));

        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(outcome);
        record.Error.Should().Be("stopped on request");
    }

    [Fact]
    public async Task AParkedRun_IsRecordedAsWaitingRatherThanAsFinishedOrFaulted()
    {
        // Parking is not an ending, and the record has to say so in every field a caller or an operator
        // reads. A CompletedAt stamp would claim work finished that has not; an Error would send someone
        // hunting for a fault when what is needed is an approval. ParkedAt is what carries the one fact
        // that is true — and it is also what bounds how long the gate may hold the workflow, so a park
        // that forgot to stamp it would park forever.
        var completion = RunCompletion.Blocked("waiting on a person");
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(completion)));

        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilAsync(
            service, store, "job-1", r => r.Status == RunStatus.Blocked);

        record.Should().NotBeNull("the run must leave Running even though it has not finished");
        record!.Status.Should().Be(RunStatus.Blocked);
        record.IsTerminal.Should().BeFalse("a parked run is live — that is what keeps its workflow locked");
        record.IsAwaitingDecision.Should().BeTrue();
        record.ParkedAt.Should().NotBeNull("without this stamp the gate can never be aged out");
        record.CompletedAt.Should().BeNull("the run has not completed");
        record.Error.Should().BeNull("waiting for an approval is not a fault");
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public async Task EveryWayARunCanEnd_TellsAnyoneWatchingThatItEnded(RunStatus outcome)
    {
        // A watcher's stream ends on this event and nothing else, so an ending that does not publish
        // one holds the client's connection — and its stream slot — open on a run that is already over.
        // Sourcing it from the planner covered only the endings the planner announces: a workflow
        // parked on a human gate, one an operator cancelled, and every run failed before the planner
        // ran at all each left a watcher waiting forever.
        var completion = outcome == RunStatus.Succeeded
            ? RunCompletion.Succeeded()
            : new RunCompletion { Status = outcome, Detail = "why it ended" };

        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(completion)));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued());

        using var watcher = Progress.Subscribe("job-1", "alice", "acme")!;
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        await DrainUntilTerminalAsync(service, store, "job-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sawFinish = false;

        await foreach (var evt in watcher.ReadAllAsync(cts.Token))
        {
            if (evt.Kind != RunProgressKind.RunFinished)
                continue;

            evt.Status.Should().Be(outcome.ToString());
            sawFinish = true;
            break;
        }

        sawFinish.Should().BeTrue("a run that ended must say so, however it ended");
    }

    [Fact]
    public async Task AParkedRun_TellsWatchersToStopWaiting_WithoutClaimingItFinished()
    {
        // Two requirements at once, and they pull in opposite directions. A watcher must be released —
        // nothing more will be published until a human acts, and an approver may take days, so a stream
        // left open holds a connection and a stream slot for all of it. But it must NOT be told the run
        // finished, because it did not: a client that recorded RunFinished here would report a workflow
        // as complete while it sits waiting for approval.
        var executor = new StubExecutor(_ =>
            Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Blocked("two approvals needed"))));

        var (service, store, queue) = Build(executor);
        Admit(store, Queued());

        using var watcher = Progress.Subscribe("job-1", "alice", "acme")!;
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        await DrainUntilAsync(service, store, "job-1", r => r.Status == RunStatus.Blocked);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var kinds = new List<RunProgressKind>();

        await foreach (var evt in watcher.ReadAllAsync(cts.Token))
        {
            kinds.Add(evt.Kind);

            if (evt.Kind != RunProgressKind.RunParked)
                continue;

            evt.Status.Should().Be(nameof(RunStatus.Blocked));
            evt.Detail.Should().Be("two approvals needed", "the watcher is owed what the run is waiting for");
            break;
        }

        kinds.Should().Contain(RunProgressKind.RunParked, "a parked run must release its watchers");
        kinds.Should().NotContain(
            RunProgressKind.RunFinished, "parking is not finishing, and a watcher must not be told it was");
    }

    [Fact]
    public async Task ARunThatFailsBeforeItsWorkBegins_StillTellsAnyoneWatching()
    {
        // The endings that never reach an executor at all: a host that cannot establish the run's
        // identity, or one with no executor for the kind. The planner never runs, so a planner-sourced
        // terminal event would never fire and the watcher would wait on a run that is already failed.
        var (service, store, queue) = Build(executor: null);
        Admit(store, Queued());

        using var watcher = Progress.Subscribe("job-1", "alice", "acme")!;
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        await DrainUntilTerminalAsync(service, store, "job-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var statuses = new List<string?>();

        await foreach (var evt in watcher.ReadAllAsync(cts.Token))
        {
            if (evt.Kind != RunProgressKind.RunFinished)
                continue;

            statuses.Add(evt.Status);
            break;
        }

        statuses.Should().Equal([nameof(RunStatus.Failed)]);
    }

    [Fact]
    public async Task AnExecutorThatThrows_StillLeavesTheRunTerminal()
    {
        // The stranding case. Without a guard around the claimed run, this leaves it Running forever
        // and a caller polls a job nothing will ever finish.
        var executor = new StubExecutor(_ => throw new InvalidOperationException("boom"));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Failed);
        record.Error.Should().NotContain("boom", "the caller gets a stable reason, not exception text");
    }

    [Fact]
    public async Task AKindWithNoRegisteredExecutor_FailsThatRunRatherThanTheDispatcher()
    {
        // A wiring gap must not take the loop down: that would turn one mis-registration into an
        // outage for every other kind of work in the host.
        var (service, store, queue) = Build(executor: null);
        Admit(store, Queued("orphan"));
        Admit(store, Queued("job-1"));
        await queue.EnqueueAsync("orphan", CancellationToken.None);
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var orphan = await DrainUntilTerminalAsync(service, store, "orphan");

        orphan!.Status.Should().Be(RunStatus.Failed);

        // The reason must name the wiring gap. Letting this fall through to the generic
        // "failed unexpectedly" guard would tell an operator the run broke, when in fact the host was
        // never configured to execute that kind of work — a different problem with a different fix.
        orphan.Error.Should().Contain("cannot execute that kind of run");

        store.Get("job-1", "alice", "acme")!.IsTerminal.Should().BeTrue(
            "the dispatcher must keep draining after a run it cannot execute");
    }

    [Fact]
    public async Task ARunAlreadyClaimed_IsSkippedRatherThanRunTwice()
    {
        // Redelivery is harmless precisely because the claim is what gates execution.
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded())));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued());
        store.TryBeginRun("job-1", _time.GetUtcNow());

        await queue.EnqueueAsync("job-1", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task RunsExecuteConcurrentlyUpToTheConfiguredDegree()
    {
        // Awaiting each run before dequeuing the next makes host-wide throughput exactly one, so any
        // caller's long workflow delays every other caller's — and it makes the per-owner cap read as
        // a concurrency allowance the host never honours.
        _config.AI.WorkflowSubmission.MaxConcurrentDispatchedRuns = 3;

        var running = 0;
        var peak = 0;
        using var release = new SemaphoreSlim(0, 3);

        var executor = new StubExecutor(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.WaitAsync();
            Interlocked.Decrement(ref running);
            return Result<RunCompletion>.Success(RunCompletion.Succeeded());
        });

        var (service, store, queue) = Build(executor);
        for (var i = 0; i < 3; i++)
        {
            Admit(store, Queued($"job-{i}"));
            await queue.EnqueueAsync($"job-{i}", CancellationToken.None);
        }

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        for (var attempt = 0; attempt < 200 && Volatile.Read(in peak) < 3; attempt++)
            await Task.Delay(10);

        release.Release(3);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        peak.Should().Be(3, "the dispatcher must run up to its configured degree at once");
    }

    [Fact]
    public async Task NoMoreRunsExecuteAtOnceThanTheConfiguredDegree()
    {
        // The other half of the same property. Unbounded dispatch would let one burst of accepted work
        // start every run simultaneously, which is what the degree exists to prevent.
        _config.AI.WorkflowSubmission.MaxConcurrentDispatchedRuns = 2;

        var running = 0;
        var peak = 0;
        using var release = new SemaphoreSlim(0, 6);

        var executor = new StubExecutor(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.WaitAsync();
            Interlocked.Decrement(ref running);
            return Result<RunCompletion>.Success(RunCompletion.Succeeded());
        });

        var (service, store, queue) = Build(executor);
        for (var i = 0; i < 6; i++)
        {
            Admit(store, Queued($"job-{i}"));
            await queue.EnqueueAsync($"job-{i}", CancellationToken.None);
        }

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Waits for dispatch to saturate rather than sleeping a fixed interval, then gives the loop a
        // brief window to overshoot if it is going to. Six jobs are already queued before the service
        // starts, so an unbounded loop would have all six running by the end of that window — the
        // recorded peak is what catches it.
        for (var attempt = 0; attempt < 200 && Volatile.Read(in peak) < 2; attempt++)
            await Task.Delay(10);

        await Task.Delay(50);

        peak.Should().Be(2, "dispatch must reach the configured degree and must never exceed it");
        Volatile.Read(in running).Should().BeLessThanOrEqualTo(2);

        release.Release(6);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int seen;
        while ((seen = Volatile.Read(in target)) < candidate
               && Interlocked.CompareExchange(ref target, candidate, seen) != seen)
        {
            // Another thread moved the peak while we were deciding; re-read and try again.
        }
    }

    [Fact]
    public async Task AnUnknownJobId_IsSkippedWithoutStoppingTheLoop()
    {
        var executor = new StubExecutor(_ => Task.FromResult(Result<RunCompletion>.Success(RunCompletion.Succeeded())));
        var (service, store, queue) = Build(executor);
        Admit(store, Queued("real"));

        await queue.EnqueueAsync("never-existed", CancellationToken.None);
        await queue.EnqueueAsync("real", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "real");

        record!.Status.Should().Be(RunStatus.Succeeded);
    }
}
