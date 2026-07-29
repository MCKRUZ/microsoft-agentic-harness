using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="RunRecordCleanupService"/>.
/// </summary>
/// <remarks>
/// The store knows how to reclaim expired runs; the question here is whether anything ever asks it to.
/// Without a caller the configured retention is a claim the host never honours, and every finished run
/// — each holding the capability envelope it executed under — is held for the life of the process.
/// That is exactly the shape of defect that hides from unit tests: the reclaiming logic is covered, so
/// coverage looks complete while nothing invokes it.
/// </remarks>
public sealed class RunRecordCleanupServiceTests
{
    /// <summary>
    /// Counts sweeps and what they reclaimed. Both are needed: an expired record is already hidden
    /// from <see cref="Get"/> before anything reclaims it, so absence proves nothing about whether the
    /// memory was ever given back.
    /// </summary>
    private sealed class CountingStore(IRunJobStore inner) : IRunJobStore
    {
        public int Sweeps { get; private set; }

        public int Reclaimed { get; private set; }

        public RunAdmission TryCreate(RunRecord record, int maxActiveRunsPerOwner) =>
            inner.TryCreate(record, maxActiveRunsPerOwner);

        public RunRecord? Get(string jobId, string ownerId, string? tenantId) =>
            inner.Get(jobId, ownerId, tenantId);

        public RunRecord? TryBeginRun(string jobId, DateTimeOffset startedAt) =>
            inner.TryBeginRun(jobId, startedAt);

        public bool Update(RunRecord record) => inner.Update(record);

        public RunRecord? FindLiveRunForTarget(RunKind kind, string targetId) =>
            inner.FindLiveRunForTarget(kind, targetId);

        /// <summary>The ceilings the service asked the store to apply, in order.</summary>
        public List<TimeSpan> ParkedCeilings { get; } = [];

        public IReadOnlyList<string> ExpireStaleParkedRuns(TimeSpan maxParkedDuration)
        {
            // Recorded rather than merely forwarded: the ceiling reaching the store is the whole of
            // what this service contributes to the parked-run rule, and a service that read the wrong
            // config value would forward a plausible-looking TimeSpan that expired nothing.
            ParkedCeilings.Add(maxParkedDuration);
            return inner.ExpireStaleParkedRuns(maxParkedDuration);
        }

        public IReadOnlyList<string> SweepExpired()
        {
            Sweeps++;
            var removed = inner.SweepExpired();
            Reclaimed += removed.Count;
            return removed;
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    [Fact]
    public async Task TheServiceActuallyReclaimsFinishedRunsPastTheirRetention()
    {
        _config.AI.WorkflowSubmission.RunRecordTtl = TimeSpan.FromMinutes(5);

        // Below the service's own floor, which clamps it to one second — the shortest a real sweep can
        // be scheduled, and short enough for a test to observe one.
        _config.AI.WorkflowSubmission.RunSweepInterval = TimeSpan.Zero;

        var monitor = new StaticOptionsMonitor<AppConfig>(_config);
        var store = new CountingStore(new InMemoryRunJobStore(monitor, _time));
        var progress = new InMemoryRunProgressBroker(monitor, _time);

        store.TryCreate(Queued("finished"), int.MaxValue).Should().Be(RunAdmission.Accepted);
        store.TryCreate(Queued("still-going"), int.MaxValue).Should().Be(RunAdmission.Accepted);

        var claimed = store.TryBeginRun("finished", _time.GetUtcNow())!;
        store.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromHours(1));

        var sut = new RunRecordCleanupService(
            store, progress, monitor, NullLogger<RunRecordCleanupService>.Instance);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        for (var attempt = 0; attempt < 60 && store.Sweeps == 0; attempt++)
            await Task.Delay(100);

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        store.Sweeps.Should().BeGreaterThan(0, "nothing reclaims runs unless this service asks");
        store.Reclaimed.Should().Be(1, "the finished run was past its retention and its memory is owed back");
        store.Get("still-going", "alice", null).Should().NotBeNull(
            "an unfinished run a caller is still polling must survive every sweep");
    }

    [Fact]
    public async Task TheSweepAlsoReleasesTheProgressBookkeepingForTheRunsItReclaims()
    {
        // A run identifier keys more than its record. Reclaiming one without the other trades a
        // bounded leak for an unbounded one: the broker would hold an entry for every run the host
        // ever streamed, for the life of the process. This is the shape of defect that hides — the
        // reclaiming method existed, was documented as being called here, and had no caller at all.
        _config.AI.WorkflowSubmission.RunRecordTtl = TimeSpan.FromMinutes(5);
        _config.AI.WorkflowSubmission.RunSweepInterval = TimeSpan.Zero;

        var monitor = new StaticOptionsMonitor<AppConfig>(_config);
        var store = new CountingStore(new InMemoryRunJobStore(monitor, _time));
        var progress = new RecordingBroker(new InMemoryRunProgressBroker(monitor, _time));

        store.TryCreate(Queued("finished"), int.MaxValue).Should().Be(RunAdmission.Accepted);
        var claimed = store.TryBeginRun("finished", _time.GetUtcNow())!;
        store.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromHours(1));

        var sut = new RunRecordCleanupService(
            store, progress, monitor, NullLogger<RunRecordCleanupService>.Instance);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        for (var attempt = 0; attempt < 60 && progress.Forgotten.Count == 0; attempt++)
            await Task.Delay(100);

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        progress.Forgotten.Should().Contain("finished");
    }

    /// <summary>Records which runs the sweeper asked the broker to forget.</summary>
    private sealed class RecordingBroker(IRunProgressBroker inner) : IRunProgressBroker
    {
        public List<string> Forgotten { get; } = [];

        public void Publish(
            string jobId,
            RunProgressKind kind,
            string? stepId = null,
            string? stepName = null,
            string? status = null,
            string? detail = null) =>
            inner.Publish(jobId, kind, stepId, stepName, status, detail);

        public IRunProgressSubscription? Subscribe(string jobId, string ownerId, string? tenantId) =>
            inner.Subscribe(jobId, ownerId, tenantId);

        public void Forget(string jobId)
        {
            lock (Forgotten)
                Forgotten.Add(jobId);

            inner.Forget(jobId);
        }
    }

    private RunRecord Queued(string jobId) => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = "alice",
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };
}
