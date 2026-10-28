using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Right-to-erasure and export for <see cref="InMemoryRunJobStore"/> (#926).
/// </summary>
/// <remarks>
/// A run record names the person who started it, so erasure must remove theirs and nobody else's, must never remove a
/// run still live (its dispatcher, its watchers and its approvals depend on the record), and must release what other
/// holders keep under a removed run's id, or erasure would strand state the retention sweep would otherwise have freed.
/// </remarks>
public sealed class InMemoryRunJobStoreComplianceTests
{
    private const string Subject = "Alice-Oid";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingListener _listener = new();

    private InMemoryRunJobStore BuildSut() =>
        new(new StaticOptionsMonitor<AppConfig>(new AppConfig()), _time, [_listener], NullLogger<InMemoryRunJobStore>.Instance);

    private RunRecord Run(string jobId, string ownerId, string? tenantId = null) => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = jobId + "-target",
        OwnerId = ownerId,
        TenantId = tenantId,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow(),
    };

    private static void Admit(InMemoryRunJobStore store, RunRecord record) =>
        store.TryCreate(record, int.MaxValue).Should().Be(RunAdmission.Accepted);

    private static void Finish(InMemoryRunJobStore store, RunRecord record) =>
        store.Update(record with { Status = RunStatus.Succeeded, CompletedAt = DateTimeOffset.UnixEpoch }).Should().BeTrue();

    private static ErasureSubject SubjectNamed(string ownerId) => new(ownerId, []);

    [Fact]
    public async Task EraseSubjectAsync_FinishedRunsOfTheSubject_AreRemovedInEveryTenantAndOthersKept()
    {
        var sut = BuildSut();
        var mine = Run("mine", Subject, tenantId: "tenant-a");
        var mineElsewhere = Run("mine-elsewhere", Subject.ToLowerInvariant(), tenantId: "tenant-b");
        var theirs = Run("theirs", "bob-oid", tenantId: "tenant-a");
        foreach (var run in new[] { mine, mineElsewhere, theirs })
        {
            Admit(sut, run);
            Finish(sut, run);
        }

        var removed = await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        removed.Should().Be(2, "both of the subject's finished runs go, whatever tenant and owner form they were stored under");
        sut.Get("mine", Subject, "tenant-a").Should().BeNull();
        sut.Get("mine-elsewhere", Subject, "tenant-b").Should().BeNull();
        sut.Get("theirs", "bob-oid", "tenant-a").Should().NotBeNull("another person's run is never erased");
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Blocked)]
    public async Task EraseSubjectAsync_RunNotEnded_IsKeptAndCountedAsLive(RunStatus status)
    {
        // A running run's record is what locks its target while the dispatcher executes it; a parked one carries the
        // approvals only the cancel path withdraws; a queued one is a run its caller is still waiting on.
        var sut = BuildSut();
        var live = Run("live", Subject);
        Admit(sut, live);
        sut.Update(live with { Status = status, ParkedAt = status == RunStatus.Blocked ? _time.GetUtcNow() : null });

        var removed = await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        removed.Should().Be(0);
        sut.Get("live", Subject, null).Should().NotBeNull();
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(1,
            "the receipt must be told a run of the subject's was kept");
        _listener.Released.Should().BeEmpty("nothing was removed, so nothing keyed by the run may be released");
    }

    [Fact]
    public async Task EraseSubjectAsync_RemovedRuns_AreReleasedFromEveryOtherHolder()
    {
        var sut = BuildSut();
        var mine = Run("mine", Subject);
        Admit(sut, mine);
        Finish(sut, mine);
        var theirs = Run("theirs", "bob-oid");
        Admit(sut, theirs);
        Finish(sut, theirs);

        await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        _listener.Released.Should().Equal(["mine"],
            "progress bookkeeping and an evaluation's report are keyed by the run's id and would otherwise outlive it");
    }

    [Fact]
    public async Task CountRunsKeptAsync_EveryRecordOfTheOwner_IsCountedWhateverItsStatusAndNoOneElses()
    {
        // A run live when the store was erased may have ended before the receipt asks: its record is still held.
        var sut = BuildSut();
        var finished = Run("finished", Subject);
        Admit(sut, finished);
        Finish(sut, finished);
        Admit(sut, Run("queued", Subject.ToUpperInvariant(), tenantId: "tenant-b"));
        Admit(sut, Run("theirs", "bob-oid"));

        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task EraseSubjectAsync_AHolderFailsToRelease_ThrowsAfterTellingEveryOther()
    {
        // The records are gone, so a holder that kept its entry (an evaluation's report) has nothing left that leads to
        // it; the receipt must say the store failed rather than read erased.
        var after = new RecordingListener();
        var sut = new InMemoryRunJobStore(
            new StaticOptionsMonitor<AppConfig>(new AppConfig()), _time, [new ThrowingListener(), after],
            NullLogger<InMemoryRunJobStore>.Instance);
        var mine = Run("mine", Subject);
        Admit(sut, mine);
        Finish(sut, mine);

        var erase = () => sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        await erase.Should().ThrowAsync<InvalidOperationException>();
        after.Released.Should().Equal(["mine"], "one holder failing must not stop the rest being told");
    }

    [Fact]
    public async Task ExportSubjectAsync_EveryRunOfTheSubject_IsCopiedLiveOrFinished()
    {
        var sut = BuildSut();
        var finished = Run("finished", Subject, tenantId: "tenant-a");
        Admit(sut, finished);
        Finish(sut, finished);
        Admit(sut, Run("queued", Subject.ToUpperInvariant()));
        Admit(sut, Run("theirs", "bob-oid"));

        var exported = await sut.ExportSubjectAsync(new ExportSubject(Subject, []), CancellationToken.None);

        exported.Select(e => e.GetProperty("jobId").GetString()).Should().BeEquivalentTo(["finished", "queued"]);
        exported.Should().AllSatisfy(e => e.GetProperty("targetId").GetString().Should().NotBeNullOrEmpty());
        exported.Single(e => e.GetProperty("jobId").GetString() == "finished")
            .GetProperty("status").GetString().Should().Be(nameof(RunStatus.Succeeded), "statuses are exported by name");
    }

    private sealed class ThrowingListener : IRunReclaimListener
    {
        public void OnRunsReclaimed(IReadOnlyList<string> jobIds) => throw new InvalidOperationException("holder broke");
    }

    private sealed class RecordingListener : IRunReclaimListener
    {
        public List<string> Released { get; } = [];

        public void OnRunsReclaimed(IReadOnlyList<string> jobIds) => Released.AddRange(jobIds);
    }
}
