using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private IRunJobStore BuildSut(TimeSpan? ttl = null)
    {
        _config.AI.WorkflowSubmission.RunRecordTtl = ttl ?? TimeSpan.FromHours(1);
        return new InMemoryRunJobStore(new StaticOptionsMonitor<AppConfig>(_config), _time);
    }

    private RunRecord Queued(
        string jobId = "job-1", string ownerId = "alice", string? targetId = null) => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = targetId ?? Guid.NewGuid().ToString(),
        OwnerId = ownerId,
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
        sut.Get("job-2", "alice").Should().BeNull("a refused run must not be stored");
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

        sut.Get("job-1", "mallory").Should().BeNull();
        sut.Get("no-such-job", "mallory").Should().BeNull();
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void Get_TreatsAnOwnerAsTheSamePrincipalRegardlessOfCasing()
    {
        // The same canonicalization the plan store applies. Comparing more strictly here would deny a
        // caller its own run whenever a token differed only in casing from the one that started it,
        // while the plan store went on treating the two as one principal.
        var sut = BuildSut();
        Admit(sut, Queued(ownerId: "Alice"));

        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void SweepExpired_NeverReclaimsAnUnfinishedRun()
    {
        // A queued or running job the caller is still polling must not vanish, however long it takes.
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        Admit(sut, Queued());

        _time.Advance(TimeSpan.FromDays(7));

        sut.SweepExpired().Should().Be(0);
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void SweepExpired_ReclaimsAFinishedRunOnlyAfterItsRetentionElapses()
    {
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        Admit(sut, Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromMinutes(4));
        sut.SweepExpired().Should().Be(0);

        _time.Advance(TimeSpan.FromMinutes(2));
        sut.SweepExpired().Should().Be(1);
        sut.Get("job-1", "alice").Should().BeNull();
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

        sut.SweepExpired().Should().Be(0, "retention runs from completion, not from acceptance");
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.Blocked)]
    public void AnyOutcomeReleasesTheWorkflowAndTheOwnersCapacity(RunStatus outcome)
    {
        // Every way a run can end has to free what it held. An outcome treated as still-live would pin
        // its workflow permanently and consume one of the caller's slots for the process lifetime —
        // which is what happens if a new RunStatus is added and IsTerminal is not updated to match.
        var sut = BuildSut();
        var workflow = Guid.NewGuid().ToString();
        Admit(sut, Queued("job-1", targetId: workflow), cap: 1);

        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = outcome, CompletedAt = _time.GetUtcNow() });

        sut.TryCreate(Queued("job-2", targetId: workflow), maxActiveRunsPerOwner: 1)
            .Should().Be(RunAdmission.Accepted);
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
