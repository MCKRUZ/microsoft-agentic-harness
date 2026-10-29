using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// The replay service (#995) writes held compliance records into the trail when the host starts and again every minute,
/// so a record held because the chain was busy reaches the trail without anyone acting on it.
/// </summary>
public sealed class ComplianceAuditReplayServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"compliance-replay-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder => Path.Combine(_root, "compliance");

    private JsonlComplianceAuditTrail NewTrail() => new(
        new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig { AI = new() { Erasure = new() { AuditStoragePath = _root } } }),
        _clock,
        AuditPseudonyms.Create(),
        NullLogger<JsonlComplianceAuditTrail>.Instance);

    private static AuditEntry Entry(string phase) => new()
    {
        RequestType = "EraseMyDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = "data-subject",
        CorrelationId = "corr-1",
        Timestamp = DateTimeOffset.UnixEpoch,
        Outcome = AuditOutcome.Attempted,
        Metadata = new Dictionary<string, string> { ["phase"] = phase },
    };

    private async Task HoldOneAsync(JsonlComplianceAuditTrail trail, string phase)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
        await trail.RecordAsync(Entry(phase), ErasureActor.DataSubject, CancellationToken.None);
        File.Delete(Folder);
    }

    // advanceClock: the service may not have started waiting yet when the clock first moves, so keep moving it.
    private async Task<long> VerifiedCountWithinAsync(JsonlComplianceAuditTrail trail, long expected, bool advanceClock = false)
    {
        var deadline = DateTime.UtcNow + Patience;
        long count;
        do
        {
            count = (await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None)).VerifiedCount;
            if (count >= expected)
                return count;
            if (advanceClock)
                _clock.Advance(ComplianceAuditReplayService.Interval);
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);
        return count;
    }

    // A pass creates the replay lock and releases it when done, so a lock that exists and can be taken means one has run.
    private async Task StartUpPassFinishedAsync()
    {
        var lockPath = Path.Combine(_root, "compliance-pending", ".replay.lock");
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(lockPath))
                {
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        return;
                }
            }
            catch (IOException)
            {
                // The pass still holds it.
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The start-up replay pass never ran.");
    }

    [Fact]
    public async Task ExecuteAsync_OnStart_WritesWhatTheLastRunHeld()
    {
        using var trail = NewTrail();
        await HoldOneAsync(trail, "attempt");
        using var service = new ComplianceAuditReplayService(trail, _clock, NullLogger<ComplianceAuditReplayService>.Instance);

        await service.StartAsync(CancellationToken.None);

        (await VerifiedCountWithinAsync(trail, 1)).Should().Be(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_EveryInterval_WritesRecordsHeldSinceTheLastPass()
    {
        using var trail = NewTrail();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        using var service = new ComplianceAuditReplayService(trail, _clock, NullLogger<ComplianceAuditReplayService>.Instance);
        await service.StartAsync(CancellationToken.None);

        // The start-up pass runs while the trail is still broken; only a later pass can write the record.
        await StartUpPassFinishedAsync();
        File.Delete(Folder);

        (await VerifiedCountWithinAsync(trail, 1, advanceClock: true)).Should().Be(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AReplacedTrail_IsLeftAlone()
    {
        var replaced = new RecordingTrail();
        using var service = new ComplianceAuditReplayService(replaced, _clock, NullLogger<ComplianceAuditReplayService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Patience);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue("a trail it does not own has no pending folder to replay");
        replaced.Calls.Should().Be(0);
    }

    private sealed class RecordingTrail : IComplianceAuditTrail
    {
        public int Calls { get; private set; }

        public Task<Result> RecordAsync(AuditEntry entry, ErasureActor actor, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Success());
        }
    }
}
