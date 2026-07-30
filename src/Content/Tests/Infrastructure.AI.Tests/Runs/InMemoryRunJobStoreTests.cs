using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="InMemoryRunJobStore"/>.
/// </summary>
/// <remarks>
/// Three properties carry real consequences and are tested hardest: a run must be armed exactly once,
/// because duplicate execution here is duplicate model and tool spend rather than a duplicate row; a
/// workflow must never have two live runs, because its execution state is keyed by the workflow and a
/// second run would share that state machine with the first; and a run must be invisible to anyone but
/// its owner, because the job identifier is the only thing standing between callers.
/// </remarks>
public sealed class InMemoryRunJobStoreTests
{
    private const int UncappedOwner = int.MaxValue;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private IRunJobStore BuildSut(TimeSpan? ttl = null)
    {
        _config.AI.WorkflowSubmission.RunRecordTtl = ttl ?? TimeSpan.FromHours(1);
        return new InMemoryRunJobStore(new StaticOptionsMonitor<AppConfig>(_config), _time);
    }

    private RunRecord Queued(
        string jobId = "job-1",
        string ownerId = "alice",
        string? targetId = null,
        string? tenantId = null) => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = targetId ?? Guid.NewGuid().ToString(),
        OwnerId = ownerId,
        TenantId = tenantId,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };

    /// <summary>Admits a run, asserting it was accepted, and returns it.</summary>
    private static RunRecord Admit(IRunJobStore store, RunRecord record, int cap = UncappedOwner)
    {
        store.TryCreate(record, cap).Should().Be(RunAdmission.Accepted);
        return record;
    }

    [Fact]
    public void TryBeginRun_UnderConcurrency_ArmsExactlyOnce()
    {
        // The property duplicate execution depends on. A redelivered queue message or a second
        // dispatcher must lose, not run the same workflow again.
        var sut = BuildSut();
        Admit(sut, Queued());

        var winners = 0;
        var threads = Enumerable.Range(0, 32).Select(_ => new Thread(() =>
        {
            if (sut.TryBeginRun("job-1", _time.GetUtcNow()) is not null)
                Interlocked.Increment(ref winners);
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        winners.Should().Be(1, "exactly one caller may claim a queued run");
    }

    [Fact]
    public void TryBeginRun_AlreadyRunning_ReturnsNull()
    {
        var sut = BuildSut();
        Admit(sut, Queued());

        sut.TryBeginRun("job-1", _time.GetUtcNow()).Should().NotBeNull();
        sut.TryBeginRun("job-1", _time.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public void TryBeginRun_RecordsTheClaimTimeAndMovesToRunning()
    {
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimedAt = _time.GetUtcNow();

        var claimed = sut.TryBeginRun("job-1", claimedAt);

        claimed!.Status.Should().Be(RunStatus.Running);
        claimed.StartedAt.Should().Be(claimedAt);
    }

    [Fact]
    public void TryCreate_ASecondRunOfALiveWorkflow_IsRefused()
    {
        // A stored workflow's execution state is keyed by the workflow, not by the run. A second
        // concurrent run would not be a second independent execution — it would resume the first one's
        // state: re-running steps the first has in flight and adopting its completed outputs.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow));

        var second = sut.TryCreate(Queued("job-2", targetId: workflow), UncappedOwner);

        second.Should().Be(RunAdmission.TargetAlreadyRunning);
        sut.Get("job-2", "alice", null).Should().BeNull("a refused run must not be stored");
    }

    [Fact]
    public void TryCreate_ASecondRunOfALiveWorkflow_IsRefusedEvenToADifferentOwner()
    {
        // The conflict is about the workflow's state, not about who asked. Scoping the check to the
        // owner would let a second caller with access to the same workflow corrupt the first's run.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", ownerId: "alice", targetId: workflow));

        sut.TryCreate(Queued("job-2", ownerId: "bob", targetId: workflow), UncappedOwner)
            .Should().Be(RunAdmission.TargetAlreadyRunning);
    }

    [Fact]
    public void TryCreate_AFurtherRunOfAWorkflow_IsAdmittedOnceTheEarlierOneEnds()
    {
        // The refusal is about live state, not a permanent lock: once the first run is terminal its
        // state is settled and the next run may resume from it.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow));

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        sut.TryCreate(Queued("job-2", targetId: workflow), UncappedOwner)
            .Should().Be(RunAdmission.Accepted);
    }

    [Fact]
    public void TryCreate_ARunOfADifferentWorkflow_IsUnaffected()
    {
        var sut = BuildSut();
        Admit(sut, Queued("job-1", targetId: Guid.NewGuid().ToString()));

        sut.TryCreate(Queued("job-2", targetId: Guid.NewGuid().ToString()), UncappedOwner)
            .Should().Be(RunAdmission.Accepted);
    }

    [Fact]
    public void TryCreate_UnderConcurrency_AdmitsOneRunPerWorkflow()
    {
        // Deciding and inserting have to be one step. Split apart, concurrent requests all observe the
        // workflow as idle and all proceed — which is exactly the state-sharing this prevents.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();

        var admitted = 0;
        var threads = Enumerable.Range(0, 32).Select(i => new Thread(() =>
        {
            if (sut.TryCreate(Queued($"job-{i}", targetId: workflow), UncappedOwner) == RunAdmission.Accepted)
                Interlocked.Increment(ref admitted);
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        admitted.Should().Be(1);
    }

    [Fact]
    public void TryCreate_WhenTheOwnerIsAtItsCap_IsRefused()
    {
        var sut = BuildSut();
        Admit(sut, Queued("job-1"), cap: 2);
        Admit(sut, Queued("job-2"), cap: 2);

        sut.TryCreate(Queued("job-3"), maxActiveRunsPerOwner: 2)
            .Should().Be(RunAdmission.OwnerAtCapacity);
    }

    [Fact]
    public void TryCreate_UnderConcurrency_NeverOvershootsTheOwnersCap()
    {
        // Counting then inserting leaves a window in which every concurrent request sees room. The cap
        // is a spend control, so overshoot is real cost rather than a cosmetic off-by-one.
        var sut = BuildSut();

        var admitted = 0;
        var threads = Enumerable.Range(0, 32).Select(i => new Thread(() =>
        {
            if (sut.TryCreate(Queued($"job-{i}"), maxActiveRunsPerOwner: 3) == RunAdmission.Accepted)
                Interlocked.Increment(ref admitted);
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        admitted.Should().Be(3);
    }

    [Fact]
    public void TryCreate_CountsOnlyTheOwnersOwnLiveRunsAgainstTheCap()
    {
        var sut = BuildSut();
        Admit(sut, Queued("job-1", ownerId: "mallory"), cap: 1);

        sut.TryCreate(Queued("job-2", ownerId: "alice"), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted, "another caller's load is not this caller's");
    }

    [Fact]
    public void TryCreate_DoesNotCountFinishedRunsAgainstTheCap()
    {
        var sut = BuildSut();
        Admit(sut, Queued("job-1"), cap: 1);

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        sut.TryCreate(Queued("job-2"), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted, "a finished run is history, not load");
    }

    [Fact]
    public void TryCreate_TreatsAnOwnerAsTheSamePrincipalRegardlessOfCasing()
    {
        // Ownership is canonicalized wherever else it is compared (PlannerScopeFilter). Comparing it
        // more strictly here would let one principal exceed its cap by varying the casing of a token.
        var sut = BuildSut();
        Admit(sut, Queued("job-1", ownerId: "Alice"), cap: 1);

        sut.TryCreate(Queued("job-2", ownerId: "alice"), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.OwnerAtCapacity);
    }

    [Fact]
    public void Get_ByAnotherOwner_IsIndistinguishableFromMissing()
    {
        var sut = BuildSut();
        Admit(sut, Queued(ownerId: "alice"));

        sut.Get("job-1", "mallory", null).Should().BeNull();
        sut.Get("no-such-job", "mallory", null).Should().BeNull();
        sut.Get("job-1", "alice", null).Should().NotBeNull();
    }

    [Fact]
    public void Get_ByTheSameOwnerIdInAnotherTenant_IsIndistinguishableFromMissing()
    {
        // Plan ownership is decided on tenant AND owner on this same request path. Comparing only the
        // owner here is invisible while an issuer is pinned to one tenant — and is a cross-tenant read
        // the day it is not.
        var sut = BuildSut();
        Admit(sut, Queued(ownerId: "alice", tenantId: "acme"));

        sut.Get("job-1", "alice", "other-tenant").Should().BeNull();
        sut.Get("job-1", "alice", null).Should().BeNull();
        sut.Get("job-1", "alice", "acme").Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_CountsTheSameOwnerInAnotherTenantSeparately()
    {
        // Same two legs as the read. One principal's load is its own; another tenant's caller sharing
        // an identifier string is a different principal and must not consume this one's allowance.
        var sut = BuildSut();
        Admit(sut, Queued("job-1", ownerId: "alice", tenantId: "acme"), cap: 1);

        sut.TryCreate(Queued("job-2", ownerId: "alice", tenantId: "other-tenant"), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted);
    }

    [Fact]
    public void Get_TreatsAnOwnerAsTheSamePrincipalRegardlessOfCasing()
    {
        // The same canonicalization the plan store applies. Comparing more strictly here would deny a
        // caller its own run whenever a token differed only in casing from the one that started it,
        // while the plan store went on treating the two as one principal.
        var sut = BuildSut();
        Admit(sut, Queued(ownerId: "Alice"));

        sut.Get("job-1", "alice", null).Should().NotBeNull();
    }

    [Fact]
    public void SweepExpired_NeverReclaimsAnUnfinishedRun()
    {
        // A queued or running job the caller is still polling must not vanish, however long it takes.
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        Admit(sut, Queued());

        _time.Advance(TimeSpan.FromDays(7));

        sut.SweepExpired().Should().BeEmpty();
        sut.Get("job-1", "alice", null).Should().NotBeNull();
    }

    [Fact]
    public void SweepExpired_ReclaimsAFinishedRunOnlyAfterItsRetentionElapses()
    {
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromMinutes(4));
        sut.SweepExpired().Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(2));
        sut.SweepExpired().Should().HaveCount(1);
        sut.Get("job-1", "alice", null).Should().BeNull();
    }

    [Fact]
    public void Update_RestartsRetentionFromCompletionNotAcceptance()
    {
        // A run that waited a long time in the queue still gets its full readable window afterwards.
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(10));
        Admit(sut, Queued());

        _time.Advance(TimeSpan.FromMinutes(9));
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromMinutes(5));

        sut.SweepExpired().Should().BeEmpty("retention runs from completion, not from acceptance");
        sut.Get("job-1", "alice", null).Should().NotBeNull();
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public void AnyOutcomeReleasesTheWorkflowAndTheOwnersCapacity(RunStatus outcome)
    {
        // Every way a run can END has to free what it held. An outcome treated as still-live would pin
        // its workflow permanently and consume one of the caller's slots for the process lifetime —
        // which is what happens if a new terminal RunStatus is added and IsTerminal is not updated to
        // match. Blocked is deliberately absent: it is the one outcome that is not an ending, and the
        // test below states the opposite requirement for it.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow), cap: 1);

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = outcome, CompletedAt = _time.GetUtcNow() });

        sut.TryCreate(Queued("job-2", targetId: workflow), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted);
    }

    [Fact]
    public void AParkedRunKeepsItsWorkflowLockedAndItsOwnersSlot()
    {
        // The inverse of the test above, and it is the whole reason Blocked is a live state. Plan
        // execution state is keyed by the workflow's id, so a second run of the same workflow shares
        // the first's state machine — it re-executes live steps, adopts the first's outputs, and can
        // answer the first's gate. Reporting a parked run as finished is precisely what would let that
        // second run in, so this asserts the refusal rather than the outcome's tidiness.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow), cap: 1);

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        sut.TryCreate(Queued("job-2", targetId: workflow), maxActiveRunsPerOwner: 1)
            .Should().Be(
                RunAdmission.TargetAlreadyRunning,
                "a workflow waiting on an approval is still in flight, and a second run would share its plan state");

        sut.TryCreate(Queued("job-3", targetId: Guid.NewGuid().ToString()), maxActiveRunsPerOwner: 1)
            .Should().Be(
                RunAdmission.OwnerAtCapacity,
                "the parked run still occupies one of the owner's slots, because the work is still live");
    }

    [Fact]
    public void AParkedRunIsNeverReclaimedByRetention()
    {
        // Retention only reclaims terminal runs, so a parked one must survive the sweep however long it
        // waits. If it did not, a caller polling a workflow that is waiting on its own approver would
        // find the run had silently vanished.
        var sut = BuildSut();
        Admit(sut, Queued("job-1"));

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromDays(365));

        sut.SweepExpired().Should().BeEmpty("a parked run has not finished, so retention does not apply");
        sut.Get("job-1", "alice", null).Should().NotBeNull();
    }

    [Fact]
    public void AGateNobodyAnswers_IsEventuallyFailedAndReleasesWhatItHeld()
    {
        // The price of Blocked being live: retention cannot reclaim it, so without a ceiling one
        // unanswered gate holds a workflow and an owner's slot for the life of the process. Expiring it
        // fails the run — rather than deleting it, so a caller coming back still learns what happened.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow), cap: 1);

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        var ceiling = TimeSpan.FromDays(7);

        sut.ExpireStaleParkedRuns(ceiling).Should().BeEmpty("the gate has only just been raised");

        _time.Advance(ceiling + TimeSpan.FromMinutes(1));

        sut.ExpireStaleParkedRuns(ceiling).Should().BeEquivalentTo(["job-1"]);

        var expired = sut.Get("job-1", "alice", null)!;
        expired.Status.Should().Be(RunStatus.Failed);
        expired.IsTerminal.Should().BeTrue();
        expired.Error.Should().NotBeNullOrWhiteSpace("a caller is owed a reason it can act on");
        expired.CompletedAt.Should().NotBeNull();

        sut.TryCreate(Queued("job-2", targetId: workflow), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted, "expiring the run must release the workflow it held");
    }

    [Fact]
    public void ExpireStaleParkedRuns_LeavesRunsThatAreNotParked()
    {
        // Scoped strictly to parked runs. A queued or running run has no ParkedAt and is not waiting on
        // anyone, so ageing it out would kill work that is making progress.
        var sut = BuildSut();
        Admit(sut, Queued("job-queued"));
        Admit(sut, Queued("job-running"));
        sut.TryBeginRun("job-running", _time.GetUtcNow());

        _time.Advance(TimeSpan.FromDays(365));

        sut.ExpireStaleParkedRuns(TimeSpan.FromDays(7)).Should().BeEmpty();
        sut.Get("job-queued", "alice", null)!.Status.Should().Be(RunStatus.Queued);
        sut.Get("job-running", "alice", null)!.Status.Should().Be(RunStatus.Running);
    }

    [Fact]
    public void TryResume_ReturnsAParkedRunToTheQueueAndClearsWhatItWasWaitingFor()
    {
        // The transition the whole gate feature turns on. Queued is the only status a dispatcher will
        // claim, so a resume that left the run in any other state would enqueue an id nothing acts on.
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        var escalation = Guid.NewGuid();
        sut.Update(claimed with
        {
            Status = RunStatus.Blocked,
            ParkedAt = _time.GetUtcNow(),
            AwaitingEscalationIds = [escalation]
        });

        var resumed = sut.TryResume("job-1");

        resumed.Should().NotBeNull();
        resumed!.Status.Should().Be(RunStatus.Queued);
        resumed.ParkedAt.Should().BeNull("the wait this recorded is over");
        resumed.AwaitingEscalationIds.Should().BeEmpty(
            "a run that is running again is not waiting on anyone, and leaving the id would resume it "
            + "on the same verdict on every later pass");
    }

    [Fact]
    public void AResumedRunKeepsTheJobIdAndTheEnvelopeItWasAcceptedUnder()
    {
        // The caller's contract: one submission, one id, whatever happens in between. And the grant is
        // the one resolved when the run was accepted — a gate approval authorizes the work to continue,
        // not to continue with anything more than it started with.
        var sut = BuildSut();
        var original = Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        var resumed = sut.TryResume("job-1")!;

        resumed.JobId.Should().Be("job-1");
        resumed.Envelope.Should().BeSameAs(original.Envelope);
        resumed.OwnerId.Should().Be(original.OwnerId);
    }

    [Fact]
    public void TryResume_UnderConcurrency_ReleasesExactlyOnce()
    {
        // Same hazard TryBeginRun exists for. Two resumers both observing the run as parked would both
        // enqueue it, and the second dispatch would execute the same plan alongside the first — two
        // schedulers writing one plan's step states.
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        var winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (sut.TryResume("job-1") is not null)
                Interlocked.Increment(ref winners);
        });

        winners.Should().Be(1);
    }

    [Fact]
    public void TryResume_RefusesARunThatIsNotParked()
    {
        // Resuming is only ever a transition out of Blocked. Applied to a finished run it would
        // resurrect completed work; applied to a running one it would queue a second execution of a
        // plan already in flight.
        var sut = BuildSut();
        Admit(sut, Queued("job-running"));
        sut.TryBeginRun("job-running", _time.GetUtcNow());

        Admit(sut, Queued("job-done"));
        var done = sut.TryBeginRun("job-done", _time.GetUtcNow())!;
        sut.Update(done with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        sut.TryResume("job-running").Should().BeNull();
        sut.TryResume("job-done").Should().BeNull();
        sut.TryResume("job-never-existed").Should().BeNull();
    }

    [Fact]
    public void AResumedRunDoesNotHaveItsStartTimeRewritten()
    {
        // StartedAt answers "when did this work begin", and a caller reads it to know how long the run
        // has been going. Restamping it on resume would report a workflow that ran for an hour, waited
        // a day for approval, and then finished as having started after the approver answered.
        var sut = BuildSut();
        Admit(sut, Queued());
        var firstStart = _time.GetUtcNow();
        var claimed = sut.TryBeginRun("job-1", firstStart)!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromDays(1));
        sut.TryResume("job-1");
        var reclaimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;

        reclaimed.StartedAt.Should().Be(firstStart);
    }

    [Fact]
    public void GetParkedRuns_ListsOnlyRunsActuallyWaitingOnADecision()
    {
        // What the resume check iterates. Including a queued or running run would have it asking the
        // escalation service about work nobody is gating; missing a parked one would leave that run to
        // the ceiling however promptly its approver answered.
        var sut = BuildSut();
        Admit(sut, Queued("job-parked"));
        var claimed = sut.TryBeginRun("job-parked", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        Admit(sut, Queued("job-queued"));
        Admit(sut, Queued("job-running"));
        sut.TryBeginRun("job-running", _time.GetUtcNow());

        sut.GetParkedRuns().Select(run => run.JobId).Should().BeEquivalentTo(["job-parked"]);
    }

    [Fact]
    public void TryCancel_ReturnsWhatTheRunWasDoing_SoItsApprovalsCanBeWithdrawn()
    {
        // The previous record, not the updated one. Reading the awaited approvals separately beforehand
        // is not the same thing: a run can park between that read and the cancel, and the caller would
        // then withdraw nothing while believing it had.
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        var escalation = Guid.NewGuid();
        sut.Update(claimed with
        {
            Status = RunStatus.Blocked,
            ParkedAt = _time.GetUtcNow(),
            AwaitingEscalationIds = [escalation]
        });

        var previous = sut.TryCancel("job-1", _time.GetUtcNow());

        previous.Should().NotBeNull();
        previous!.Status.Should().Be(RunStatus.Blocked);
        previous.AwaitingEscalationIds.Should().Equal([escalation]);

        var now = sut.Get("job-1", "alice", null)!;
        now.Status.Should().Be(RunStatus.Cancelled);
        now.IsTerminal.Should().BeTrue();
        now.CompletedAt.Should().NotBeNull();
        now.ParkedAt.Should().BeNull("the run is no longer waiting on anyone");
    }

    [Fact]
    public void ACancelledRunReleasesTheWorkflowItHeld()
    {
        // A cancelled run that kept its workflow locked would leave the caller unable to start the
        // replacement its cancellation was for — and the run it is waiting on is one it just stopped.
        var workflow = Guid.NewGuid().ToString();
        var sut = BuildSut();
        Admit(sut, Queued(targetId: workflow));
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        sut.TryCancel("job-1", _time.GetUtcNow()).Should().NotBeNull();

        sut.TryCreate(Queued("job-2", targetId: workflow), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted);
    }

    [Fact]
    public void ACancelledRunIsReadableForAFullRetentionWindowFromWhenItWasCancelled()
    {
        // Retention runs from the ending, not from admission — the same rule every other outcome
        // follows. A run parked for longer than the window before being cancelled would otherwise be
        // reclaimed on the very next sweep, so the caller that cancelled it would poll and be told no
        // such run exists rather than that its cancellation worked.
        var ttl = TimeSpan.FromMinutes(5);
        var sut = BuildSut(ttl: ttl);
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        // Long past the expiry the entry was seeded with at admission.
        _time.Advance(TimeSpan.FromHours(1));
        sut.TryCancel("job-1", _time.GetUtcNow()).Should().NotBeNull();

        sut.SweepExpired().Should().NotContain("job-1", "the run was cancelled a moment ago");
        sut.Get("job-1", "alice", null).Should().NotBeNull();

        _time.Advance(ttl + TimeSpan.FromMinutes(1));
        sut.SweepExpired().Should().Contain("job-1", "and it is reclaimed once its own window elapses");
    }

    [Fact]
    public void TryCancel_RefusesARunningRun_BecauseItsOutcomeBelongsToTheDispatchExecutingIt()
    {
        // Writing a terminal state here would be overwritten moments later by the dispatch that holds
        // the run — so it would read as cancelled and then silently revert to whatever the work did.
        var sut = BuildSut();
        Admit(sut, Queued());
        sut.TryBeginRun("job-1", _time.GetUtcNow());

        sut.TryCancel("job-1", _time.GetUtcNow()).Should().BeNull();
        sut.Get("job-1", "alice", null)!.Status.Should().Be(RunStatus.Running);
    }

    [Fact]
    public void TryCancel_UnderConcurrency_CancelsExactlyOnce()
    {
        // Only one caller may believe it cancelled the run, because that caller is the one that then
        // withdraws the approvals — and withdrawing twice would report a withdrawal to an approver that
        // had already been made.
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        var winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (sut.TryCancel("job-1", _time.GetUtcNow()) is not null)
                Interlocked.Increment(ref winners);
        });

        winners.Should().Be(1);
    }

    [Fact]
    public void ACancelledRunIsNeverResumed()
    {
        // The two transitions have to be mutually exclusive. A verdict arriving on a withdrawn approval
        // must not put a run the caller stopped back to work.
        var sut = BuildSut();
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Blocked, ParkedAt = _time.GetUtcNow() });

        sut.TryCancel("job-1", _time.GetUtcNow()).Should().NotBeNull();

        sut.TryResume("job-1").Should().BeNull();
        sut.GetParkedRuns().Should().BeEmpty();
    }

    [Fact]
    public void TryCreate_WithADuplicateJobId_Throws()
    {
        var sut = BuildSut();
        Admit(sut, Queued(targetId: "target-a"));

        var act = () => sut.TryCreate(Queued(targetId: "target-b"), UncappedOwner);

        act.Should().Throw<InvalidOperationException>();
    }
}
