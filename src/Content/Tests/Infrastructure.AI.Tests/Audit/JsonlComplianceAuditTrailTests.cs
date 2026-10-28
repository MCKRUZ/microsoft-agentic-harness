using System.Text.Json;
using Application.Core.Compliance;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// The compliance audit trail (#969): every erasure, export and document-removal record is kept hash-chained in its own
/// day-files, where tampering is detected, and a named operator is recorded only as a keyed reference.
/// </summary>
public sealed class JsonlComplianceAuditTrailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"compliance-audit-{Guid.NewGuid():N}");
    private readonly HmacAuditPseudonymizer _pseudonyms = AuditPseudonyms.Create();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder => Path.Combine(_root, "compliance");

    private JsonlComplianceAuditTrail NewTrail() => new(
        new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig { AI = new() { Erasure = new() { AuditStoragePath = _root } } }),
        TimeProvider.System,
        _pseudonyms,
        NullLogger<JsonlComplianceAuditTrail>.Instance);

    private static AuditEntry Entry(string phase, AuditOutcome outcome, string executorId = "data-subject") => new()
    {
        RequestType = "EraseMyDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = executorId,
        CorrelationId = "corr-1",
        Timestamp = DateTimeOffset.UnixEpoch,
        Outcome = outcome,
        Metadata = new Dictionary<string, string> { ["phase"] = phase, ["receipt_request_id"] = "receipt-1" },
    };

    private string[] Lines() => Directory.GetFiles(Folder, "*.jsonl").Order(StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray();

    private JsonElement Record(int index) =>
        JsonDocument.Parse(HashChainedJsonlWriter.ExtractPayload(Lines()[index])).RootElement;

    [Fact]
    public async Task RecordAsync_AttemptAndOutcome_AreChainedInTheComplianceFolderAndVerify()
    {
        using var trail = NewTrail();

        await trail.RecordAsync(Entry("attempt", AuditOutcome.Attempted), ErasureActor.DataSubject, CancellationToken.None);
        await trail.RecordAsync(Entry("outcome", AuditOutcome.Success), ErasureActor.DataSubject, CancellationToken.None);

        Lines().Should().HaveCount(2);
        Record(0).GetProperty("action").GetString().Should().Be(AuditedErasure.AuditAction);
        Record(0).GetProperty("outcome").GetString().Should().Be("Attempted");
        Record(1).GetProperty("metadata").GetProperty("receipt_request_id").GetString().Should().Be("receipt-1");
        trail.AuditName.Should().Be("compliance");
        trail.AuditLocation.Should().Be(Folder);
        var verified = await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordAsync_AfterARestart_ContinuesTheSameChain()
    {
        using (var first = NewTrail())
            await first.RecordAsync(Entry("attempt", AuditOutcome.Attempted), ErasureActor.DataSubject, CancellationToken.None);

        using var second = NewTrail();
        await second.RecordAsync(Entry("outcome", AuditOutcome.Success), ErasureActor.DataSubject, CancellationToken.None);

        var verified = await second.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(2, "the proof must survive the process that wrote it");
    }

    [Fact]
    public async Task VerifyChainAsync_ARecordEditedAfterwards_IsReportedBroken()
    {
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt", AuditOutcome.Attempted), ErasureActor.DataSubject, CancellationToken.None);
        await trail.RecordAsync(Entry("outcome", AuditOutcome.Success), ErasureActor.DataSubject, CancellationToken.None);

        var file = Directory.GetFiles(Folder, "*.jsonl").Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Attempted\"", "\"Success\"", StringComparison.Ordinal));

        var verified = await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeFalse("rewriting what an erasure record says must be detectable");
        verified.FirstBrokenSequence.Should().Be(0);
    }

    [Fact]
    public async Task RecordAsync_NamedOperator_IsRecordedAsTheirAuditReferenceOnly()
    {
        using var trail = NewTrail();

        await trail.RecordAsync(Entry("attempt", AuditOutcome.Attempted, "dpo-operator-9"),
            ErasureActor.Operator("dpo-operator-9"), CancellationToken.None);

        Record(0).GetProperty("executor_id").GetString().Should().Be(_pseudonyms.Person("dpo-operator-9"),
            "the trail is kept for good, so it keeps the operator the way the other trails keep approvers");
        Lines().Single().Should().NotContain("dpo-operator-9");
    }

    [Theory]
    [InlineData("data-subject")]
    [InlineData("retention")]
    [InlineData("unidentified-operator")]
    public async Task RecordAsync_ActorThatIsNoNamedPerson_IsRecordedByItsFixedName(string executor)
    {
        var actor = new[] { ErasureActor.DataSubject, ErasureActor.Retention, ErasureActor.UnidentifiedOperator }
            .Single(a => a.ExecutorId == executor);
        using var trail = NewTrail();

        await trail.RecordAsync(Entry("outcome", AuditOutcome.Denied, executor), actor, CancellationToken.None);

        Record(0).GetProperty("executor_id").GetString().Should().Be(executor);
    }

    [Fact]
    public async Task RecordAsync_TheChainCannotBeWritten_Throws()
    {
        // A file where the day folder should be: every append fails. The callers rely on a throw to log the loss.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
        using var trail = NewTrail();

        var write = () => trail.RecordAsync(Entry("attempt", AuditOutcome.Attempted), ErasureActor.DataSubject, CancellationToken.None);

        await write.Should().ThrowAsync<IOException>();
    }
}
