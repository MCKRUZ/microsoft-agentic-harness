using System.Text.Json;
using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class JsonlDriftAuditStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly JsonlDriftAuditStore _store;

    public JsonlDriftAuditStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"drift-audit-tests-{Guid.NewGuid():N}");
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero));

        var config = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig { AuditPath = _tempDir }
            }
        };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        _store = new JsonlDriftAuditStore(options, _timeProvider, Mock.Of<ILogger<JsonlDriftAuditStore>>());
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DriftAuditRecord BuildRecord(
        Guid? eventId = null,
        DriftAuditRecordType recordType = DriftAuditRecordType.Detected,
        DateTimeOffset? recordedAt = null) => new()
    {
        RecordId = Guid.NewGuid(),
        EventId = eventId ?? Guid.NewGuid(),
        RecordType = recordType,
        Payload = JsonSerializer.Serialize(new { detail = "test" }),
        RecordedAt = recordedAt ?? _timeProvider.GetUtcNow()
    };

    [Fact]
    public async Task Record_AppendsToJsonlFile()
    {
        var record = BuildRecord();

        var result = await _store.RecordAsync(record, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl");
        File.Exists(filePath).Should().BeTrue();
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Record_CreatesDatePartitionedFile()
    {
        await _store.RecordAsync(BuildRecord(), CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromDays(1));
        await _store.RecordAsync(
            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
            CancellationToken.None);

        File.Exists(Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "drift-audit", "2025-06-16.jsonl")).Should().BeTrue();
    }

    [Fact]
    public async Task GetRecords_FiltersByDateRange()
    {
        // Day 1
        await _store.RecordAsync(BuildRecord(), CancellationToken.None);

        // Day 2
        _timeProvider.Advance(TimeSpan.FromDays(1));
        await _store.RecordAsync(
            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
            CancellationToken.None);

        // Day 3
        _timeProvider.Advance(TimeSpan.FromDays(1));
        await _store.RecordAsync(
            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
            CancellationToken.None);

        var query = new DriftAuditQuery
        {
            Start = new DateTimeOffset(2025, 6, 16, 0, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2025, 6, 16, 23, 59, 59, TimeSpan.Zero)
        };

        var result = await _store.GetRecordsAsync(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(1);
        result.Value[0].RecordedAt.Date.Should().Be(new DateTime(2025, 6, 16));
    }

    [Fact]
    public async Task GetRecords_FiltersByRecordType()
    {
        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.Detected), CancellationToken.None);
        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.Resolved), CancellationToken.None);
        await _store.RecordAsync(BuildRecord(recordType: DriftAuditRecordType.BaselineUpdated), CancellationToken.None);

        var query = new DriftAuditQuery { RecordType = DriftAuditRecordType.Detected };

        var result = await _store.GetRecordsAsync(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(1);
        result.Value[0].RecordType.Should().Be(DriftAuditRecordType.Detected);
    }

    [Fact]
    public async Task GetRecords_FiltersByEventId()
    {
        var targetEventId = Guid.NewGuid();
        await _store.RecordAsync(BuildRecord(eventId: targetEventId), CancellationToken.None);
        await _store.RecordAsync(BuildRecord(), CancellationToken.None);
        await _store.RecordAsync(BuildRecord(), CancellationToken.None);

        var query = new DriftAuditQuery { EventId = targetEventId };

        var result = await _store.GetRecordsAsync(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(1);
        result.Value[0].EventId.Should().Be(targetEventId);
    }

    [Fact]
    public async Task Record_ThreadSafe_ConcurrentWrites()
    {
        var eventIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var tasks = eventIds.Select(id =>
            _store.RecordAsync(BuildRecord(eventId: id), CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess);
        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-06-15.jsonl");
        var lines = (await File.ReadAllLinesAsync(filePath))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().HaveCount(20);

        var allRecords = await _store.GetRecordsAsync(new DriftAuditQuery(), CancellationToken.None);
        allRecords.Value!.Select(r => r.EventId).Should().BeEquivalentTo(eventIds);
    }

    [Fact]
    public async Task Record_UsesTimeProviderForTimestamp()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2025, 12, 25, 8, 0, 0, TimeSpan.Zero));
        await _store.RecordAsync(
            BuildRecord(recordedAt: _timeProvider.GetUtcNow()),
            CancellationToken.None);

        var filePath = Path.Combine(_tempDir, "drift-audit", "2025-12-25.jsonl");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task GetRecords_EmptyDirectory_ReturnsEmpty()
    {
        var result = await _store.GetRecordsAsync(new DriftAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }
}
