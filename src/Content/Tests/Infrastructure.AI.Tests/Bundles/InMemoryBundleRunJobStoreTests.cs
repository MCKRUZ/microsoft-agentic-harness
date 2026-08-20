using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="InMemoryBundleRunJobStore"/>: create/get/update snapshots, per-record TTL, and the
/// terminal-state TTL extension that keeps a completed run pollable for the full window.
/// </summary>
public sealed class InMemoryBundleRunJobStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private InMemoryBundleRunJobStore BuildSut()
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.RunRecordTtl = Ttl;
        cfg.AI.BundleExecution.StreamReservationTtl = Ttl; // same window here so the shared streaming tests read cleanly
        return new InMemoryBundleRunJobStore(new StaticOptionsMonitor<AppConfig>(cfg), _time);
    }

    private InMemoryBundleRunJobStore BuildSut(TimeSpan runRecordTtl, TimeSpan streamReservationTtl)
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.RunRecordTtl = runRecordTtl;
        cfg.AI.BundleExecution.StreamReservationTtl = streamReservationTtl;
        return new InMemoryBundleRunJobStore(new StaticOptionsMonitor<AppConfig>(cfg), _time);
    }

    private BundleRunRecord NewRecord(
        string jobId = "j1", BundleRunStatus status = BundleRunStatus.Queued, bool streaming = false,
        string ownerId = "owner-1", string? conversationId = null) => new()
    {
        JobId = jobId,
        Handle = "h1",
        OwnerId = ownerId,
        AgentName = "agent-1",
        UserMessages = ["hello"],
        MaxTurns = 5,
        Envelope = new CapabilityEnvelope(),
        Status = status,
        Streaming = streaming,
        ConversationId = conversationId,
        CreatedAt = _time.GetUtcNow()
    };

    /// <summary>Admits a record with no capacity cap, for tests that don't exercise admission itself.</summary>
    private static void Store(InMemoryBundleRunJobStore sut, BundleRunRecord record) =>
        sut.TryCreate(record, int.MaxValue);

    [Fact]
    public void Create_ThenGet_ReturnsRecord()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());

        sut.Get("j1").Should().NotBeNull();
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Queued);
    }

    [Fact]
    public void Create_DuplicateJobId_Throws()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());

        var act = () => Store(sut, NewRecord());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Get_UnknownJobId_ReturnsNull()
    {
        BuildSut().Get("nope").Should().BeNull();
    }

    [Fact]
    public void Get_NonTerminalRecord_NeverExpires()
    {
        var sut = BuildSut();
        Store(sut, NewRecord()); // Queued

        _time.Advance(Ttl + TimeSpan.FromHours(1));

        // An in-flight (Queued/Running) run is never expired — its record must survive for the dispatcher.
        sut.Get("j1").Should().NotBeNull();
    }

    [Fact]
    public void Get_TerminalRecord_AfterTtlElapses_ReturnsNull()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.Get("j1").Should().BeNull();
    }

    [Fact]
    public void Update_ReplacesSnapshot()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());

        var updated = sut.Get("j1")! with { Status = BundleRunStatus.Running };
        sut.Update(updated).Should().BeTrue();

        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Running);
    }

    [Fact]
    public void Update_TerminalStatus_ExtendsTtl()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());

        // Advance almost to the original expiry, then complete the run — which resets the TTL window.
        _time.Advance(Ttl - TimeSpan.FromMinutes(1));
        var done = sut.Get("j1")! with { Status = BundleRunStatus.Succeeded };
        sut.Update(done).Should().BeTrue();

        // Past the ORIGINAL expiry but within the extended window: still pollable.
        _time.Advance(TimeSpan.FromMinutes(2));
        sut.Get("j1").Should().NotBeNull();
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Succeeded);
    }

    [Fact]
    public void Update_UnknownJobId_ReturnsFalse()
    {
        BuildSut().Update(NewRecord("ghost")).Should().BeFalse();
    }

    [Fact]
    public void SweepExpired_RemovesExpiredTerminalRecords_ReturnsCount()
    {
        var sut = BuildSut();
        Store(sut, NewRecord("j1"));
        Store(sut, NewRecord("j2"));
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });
        sut.Update(sut.Get("j2")! with { Status = BundleRunStatus.Failed });

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.SweepExpired().Should().Be(2);
        sut.Get("j1").Should().BeNull();
        sut.Get("j2").Should().BeNull();
    }

    [Fact]
    public void SweepExpired_LeavesNonTerminalRecords()
    {
        var sut = BuildSut();
        Store(sut, NewRecord("j1")); // Queued, in-flight

        _time.Advance(Ttl + TimeSpan.FromHours(1));

        sut.SweepExpired().Should().Be(0);
        sut.Get("j1").Should().NotBeNull();
    }

    // --- TryBeginRun (atomic claim) ---

    [Fact]
    public void TryBeginRun_QueuedRecord_TransitionsToRunning_StampsStartedAt()
    {
        var sut = BuildSut();
        Store(sut, NewRecord());
        var startedAt = _time.GetUtcNow();

        var claimed = sut.TryBeginRun("j1", startedAt);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(BundleRunStatus.Running);
        claimed.StartedAt.Should().Be(startedAt);
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Running);
    }

    [Fact]
    public void TryBeginRun_UnknownJobId_ReturnsNull()
    {
        BuildSut().TryBeginRun("ghost", _time.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public void TryBeginRun_NonQueuedRecord_ReturnsNull()
    {
        var sut = BuildSut();
        Store(sut, NewRecord(status: BundleRunStatus.Running));

        sut.TryBeginRun("j1", _time.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public void TryBeginRun_CalledTwice_ClaimsExactlyOnce()
    {
        // The single guarantee that a run is driven once: whoever wins the CAS gets the record; the loser
        // gets null. This is what stops two stream connections, or a stream and the dispatcher, double-running.
        var sut = BuildSut();
        Store(sut, NewRecord());

        var first = sut.TryBeginRun("j1", _time.GetUtcNow());
        var second = sut.TryBeginRun("j1", _time.GetUtcNow());

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryBeginRun_ExpiredStreamingReservation_ReturnsNull()
    {
        var sut = BuildSut();
        Store(sut, NewRecord(streaming: true)); // unclaimed streaming reservation

        _time.Advance(Ttl + TimeSpan.FromSeconds(1)); // reservation window elapsed

        sut.TryBeginRun("j1", _time.GetUtcNow()).Should().BeNull("a lapsed reservation must not be driveable");
    }

    // --- Streaming reservation expiry ---

    [Fact]
    public void Get_UnclaimedStreamingReservation_ExpiresAfterTtl()
    {
        var sut = BuildSut();
        Store(sut, NewRecord(streaming: true)); // Queued streaming, never claimed

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        // A reservation the caller never streamed is reclaimable — unlike a background-queued run.
        sut.Get("j1").Should().BeNull();
    }

    [Fact]
    public void Get_ClaimedStreamingRun_NeverExpires()
    {
        var sut = BuildSut();
        Store(sut, NewRecord(streaming: true));
        sut.TryBeginRun("j1", _time.GetUtcNow()); // now Running

        _time.Advance(Ttl + TimeSpan.FromHours(1));

        // Once claimed, a streaming run is in-flight and is protected exactly like any other Running run.
        sut.Get("j1").Should().NotBeNull();
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Running);
    }

    [Fact]
    public void SweepExpired_RemovesUnclaimedStreamingReservation_ButNotBackgroundQueued()
    {
        var sut = BuildSut();
        Store(sut, NewRecord("stream", streaming: true)); // reclaimable once its window lapses
        Store(sut, NewRecord("bg")); // background-queued: never reclaimable until terminal

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.SweepExpired().Should().Be(1);
        sut.Get("stream").Should().BeNull();
        sut.Get("bg").Should().NotBeNull();
    }

    [Fact]
    public void StreamingReservation_ExpiryUsesStreamReservationTtl_NotRunRecordTtl()
    {
        // The connect window is its own knob: an unclaimed reservation lapses on the (short) reservation TTL,
        // NOT the (longer) result-retention TTL — so tightening result retention never shrinks the window a
        // caller has to connect.
        var sut = BuildSut(runRecordTtl: TimeSpan.FromMinutes(30), streamReservationTtl: TimeSpan.FromMinutes(2));
        Store(sut, NewRecord(streaming: true));

        _time.Advance(TimeSpan.FromMinutes(3)); // past the 2-min reservation window, far within RunRecordTtl

        sut.Get("j1").Should().BeNull();
    }

    [Fact]
    public void CompletedStreamingRun_PollableWindowUsesRunRecordTtl_NotReservationTtl()
    {
        // Once a streaming run is claimed and completes, its pollable window is the result-retention TTL like
        // any other terminal run — the short reservation window governs only the unclaimed phase.
        var sut = BuildSut(runRecordTtl: TimeSpan.FromMinutes(30), streamReservationTtl: TimeSpan.FromMinutes(2));
        Store(sut, NewRecord(streaming: true));
        sut.TryBeginRun("j1", _time.GetUtcNow());
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });

        _time.Advance(TimeSpan.FromMinutes(3)); // past the reservation window...
        sut.Get("j1").Should().NotBeNull("a completed streamed run stays pollable for the full result window");

        _time.Advance(TimeSpan.FromMinutes(30)); // ...and reclaimed only once the result window elapses
        sut.Get("j1").Should().BeNull();
    }

    // -- Admission (#449) --

    [Fact]
    public void TryCreate_SecondRunSameConversation_RefusesConflict()
    {
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1", conversationId: "conv-1"), int.MaxValue)
            .Should().Be(BundleRunAdmission.Accepted);

        var admission = sut.TryCreate(NewRecord("j2", conversationId: "conv-1"), int.MaxValue);

        admission.Should().Be(BundleRunAdmission.ConversationAlreadyRunning);
        sut.Get("j2").Should().BeNull("a refused run must not be stored");
    }

    [Fact]
    public void TryCreate_SecondRunSameConversation_ButFirstIsTerminal_IsAccepted()
    {
        var sut = BuildSut();
        Store(sut, NewRecord("j1", conversationId: "conv-1"));
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });

        var admission = sut.TryCreate(NewRecord("j2", conversationId: "conv-1"), int.MaxValue);

        admission.Should().Be(BundleRunAdmission.Accepted, "a finished run's conversation is no longer live");
    }

    [Fact]
    public void TryCreate_AbandonedStreamingReservation_DoesNotCountTowardConversationOrCapacity()
    {
        // /code-review finding: SurveyActiveRuns originally excluded only terminal records, so a
        // streaming reservation nobody ever connected to (Queued, Streaming, past its short connect
        // window) stayed "live" from admission's point of view until the next sweep pass — permanently
        // occupying a capacity slot and blocking every retry against its conversation for a run that
        // never actually did anything. Get/TryBeginRun already treated it as gone the moment it expired;
        // admission must too.
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1", conversationId: "conv-1", streaming: true), maxActiveRunsPerOwner: 1)
            .Should().Be(BundleRunAdmission.Accepted);

        _time.Advance(Ttl + TimeSpan.FromSeconds(1)); // past the reservation window, never claimed

        var admission = sut.TryCreate(NewRecord("j2", conversationId: "conv-1"), maxActiveRunsPerOwner: 1);

        admission.Should().Be(BundleRunAdmission.Accepted,
            "an abandoned streaming reservation is reclaimable the moment it expires, not only once swept");
    }

    [Fact]
    public void TryCreate_TwoRunsNoConversationId_NeverConflict()
    {
        // A one-shot run has nothing to conflict on — only a shared ConversationId can collide.
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1"), int.MaxValue).Should().Be(BundleRunAdmission.Accepted);

        sut.TryCreate(NewRecord("j2"), int.MaxValue).Should().Be(BundleRunAdmission.Accepted);
    }

    [Fact]
    public void TryCreate_OwnerAtCapacity_RefusesConflict()
    {
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1", ownerId: "owner-1"), maxActiveRunsPerOwner: 1)
            .Should().Be(BundleRunAdmission.Accepted);

        var admission = sut.TryCreate(NewRecord("j2", ownerId: "owner-1"), maxActiveRunsPerOwner: 1);

        admission.Should().Be(BundleRunAdmission.OwnerAtCapacity);
        sut.Get("j2").Should().BeNull("a refused run must not be stored");
    }

    [Fact]
    public void TryCreate_TerminalRunsDoNotCountTowardCapacity()
    {
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1", ownerId: "owner-1"), maxActiveRunsPerOwner: 1);
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });

        var admission = sut.TryCreate(NewRecord("j2", ownerId: "owner-1"), maxActiveRunsPerOwner: 1);

        admission.Should().Be(BundleRunAdmission.Accepted, "a finished run must not hold its owner's capacity slot");
    }

    [Fact]
    public void TryCreate_DifferentOwners_DoNotShareCapacity()
    {
        var sut = BuildSut();
        sut.TryCreate(NewRecord("j1", ownerId: "owner-1"), maxActiveRunsPerOwner: 1)
            .Should().Be(BundleRunAdmission.Accepted);

        var admission = sut.TryCreate(NewRecord("j2", ownerId: "owner-2"), maxActiveRunsPerOwner: 1);

        admission.Should().Be(BundleRunAdmission.Accepted, "the cap is per owner, not host-wide");
    }

    [Fact]
    public async Task TryCreate_ConcurrentCallsSameOwner_AdmitsExactlyUpToTheCap()
    {
        // Hammers TryCreate from many threads at once so the admission lock's atomicity is actually
        // exercised, not just its single-threaded happy path — TryBeginRun's twin sequential-only test
        // (TryBeginRun_CalledTwice_ClaimsExactlyOnce) does not catch a race here either.
        var sut = BuildSut();
        const int attempts = 50;
        const int cap = 10;

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(i => Task.Run(
            () => sut.TryCreate(NewRecord($"job-{i}", ownerId: "owner-1"), cap))));

        results.Count(a => a == BundleRunAdmission.Accepted).Should().Be(cap);
        results.Count(a => a == BundleRunAdmission.OwnerAtCapacity).Should().Be(attempts - cap);
    }
}
