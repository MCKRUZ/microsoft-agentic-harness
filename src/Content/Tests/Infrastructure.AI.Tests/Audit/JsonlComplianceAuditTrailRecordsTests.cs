using Application.Core.Compliance;
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
/// The compliance report reads the trail back (#1000): a record belongs to a period by its own timestamp, wherever in the
/// chain it landed, and records still held for replay are counted so the report can say they are missing.
/// </summary>
public sealed class JsonlComplianceAuditTrailRecordsTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"compliance-records-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(Day1);

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

    private AuditEntry Entry(string receiptId) => new()
    {
        RequestType = "EraseSubjectDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = "dpo-operator-1",
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = _clock.GetUtcNow(),
        Outcome = AuditOutcome.Success,
        Metadata = new Dictionary<string, string> { ["phase"] = "outcome", ["receipt_request_id"] = receiptId },
    };

    private static Task<string[]> ReceiptsAsync(JsonlComplianceAuditTrail trail, DateTimeOffset start, DateTimeOffset end) =>
        trail.ReadAsync(start, end, CancellationToken.None).ContinueWith(t =>
            t.Result.Value!.Records.Select(r => r.Metadata!["receipt_request_id"]).ToArray(), TaskScheduler.Default);

    [Fact]
    public async Task ReadAsync_RecordsOutsideThePeriod_AreLeftOut()
    {
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("before"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(2));
        await trail.RecordAsync(Entry("inside"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(2));
        await trail.RecordAsync(Entry("after"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);

        var receipts = await ReceiptsAsync(trail, Day1.AddDays(1), Day1.AddDays(3));

        receipts.Should().Equal("inside");
    }

    [Fact]
    public async Task ReadAsync_ARecordReplayedIntoALaterDayFile_CountsInThePeriodItWasMadeIn()
    {
        using var trail = NewTrail();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
        await trail.RecordAsync(Entry("replayed"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);
        File.Delete(Folder);
        _clock.Advance(TimeSpan.FromDays(3)); // Replayed three days later, into that day's file.
        (await trail.ReplayPendingAsync(CancellationToken.None)).Written.Should().Be(1);

        var receipts = await ReceiptsAsync(trail, Day1.AddHours(-1), Day1.AddHours(1));

        receipts.Should().Equal(["replayed"], "a record belongs to the period of its own timestamp, not of the file it landed in");
    }

    [Fact]
    public async Task ReadAsync_ARecordStillHeld_IsCountedAsHeldNotRead()
    {
        using var trail = NewTrail();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
        await trail.RecordAsync(Entry("held"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);
        File.Delete(Folder);

        var read = await trail.ReadAsync(Day1.AddHours(-1), Day1.AddHours(1), CancellationToken.None);

        read.IsSuccess.Should().BeTrue();
        read.Value!.Records.Should().BeEmpty();
        read.Value.HeldForReplay.Should().Be(1, "the report must say a record is missing rather than count as if complete");
    }

    [Fact]
    public async Task ReadAsync_TheTrailsFolderIsUnreadable_FailsInsteadOfReadingAsEmpty()
    {
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("kept"), ErasureActor.Operator("dpo-operator-1"), CancellationToken.None);
        // A day-file name in the wrong case makes the writer refuse the folder (#416).
        File.WriteAllText(Path.Combine(Folder, "2026-10-02.JSONL"), string.Empty);

        var read = await trail.ReadAsync(Day1.AddHours(-1), Day1.AddHours(1), CancellationToken.None);

        read.IsSuccess.Should().BeFalse();
    }
}
