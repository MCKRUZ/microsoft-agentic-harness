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

        public int SweepExpired()
        {
            Sweeps++;
            var removed = inner.SweepExpired();
            Reclaimed += removed;
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

        store.TryCreate(Queued("finished"), int.MaxValue).Should().Be(RunAdmission.Accepted);
        store.TryCreate(Queued("still-going"), int.MaxValue).Should().Be(RunAdmission.Accepted);

        var claimed = store.TryBeginRun("finished", _time.GetUtcNow())!;
        store.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromHours(1));

        var sut = new RunRecordCleanupService(store, monitor, NullLogger<RunRecordCleanupService>.Instance);

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
