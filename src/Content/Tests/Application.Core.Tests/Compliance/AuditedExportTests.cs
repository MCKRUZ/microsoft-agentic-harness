using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services.Compliance;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.ExportMyData;
using Domain.Common.Compliance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// The export audit record (#902): it proves a copy was made, for whom by reference, by whom, and what each store
/// returned, and never keeps the person's identity or anything the copy contains.
/// </summary>
public sealed class AuditedExportTests
{
    private const string Subject = "Subject-Oid-7f3a";
    private const string Content = "the person's own words";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<IPersonalDataExport> _export = new();
    private readonly List<AuditEntry> _records = [];
    private readonly Mock<IAuditSink> _sink = new();
    private readonly CapturingLogger<AuditedExport> _logger = new();
    private readonly ErasureConfig _config = new() { SubjectReferenceKey = Key };

    public AuditedExportTests()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
    }

    private HmacErasureSubjectReference Reference() =>
        new(Mock.Of<IOptionsMonitor<ErasureConfig>>(m => m.CurrentValue == _config));

    private AuditedExport Create() => new(_export.Object, Reference(), _sink.Object, TimeProvider.System, _logger);

    private static PersonalDataExport Copy() => new(
        "export-1",
        DateTimeOffset.UnixEpoch,
        [new ExportManifestEntry("conversations", ExportStatus.Included, 2), new ExportManifestEntry("plans", ExportStatus.NotExportable, 0, "x")],
        [new ExportSection("conversations", [JsonSerializer.SerializeToElement(new { text = Content })])]);

    [Fact]
    public void ExportMyDataCommand_RunsLongerThanThePipelinesDefaultLimit()
    {
        new ExportMyDataCommand().Timeout.Should().Be(AuditedExport.RequestTimeout);
        AuditedExport.RequestTimeout.Should().BeGreaterThan(TimeSpan.FromSeconds(new AgentConfig().DefaultRequestTimeoutSec),
            "the copy reads every store erasure deletes from, which can take longer than an ordinary request");
    }

    [Fact]
    public async Task ExportAsync_Succeeds_RecordsAttemptAndOutcomeWithEachStoresStatus()
    {
        var copy = Copy();
        _export.Setup(e => e.ExportOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(copy);

        var result = await Create().ExportAsync(Subject, ErasureActor.DataSubject, "ExportMyDataCommand", CancellationToken.None);

        result.Value.Should().BeSameAs(copy);
        _records.Select(r => r.Metadata!["phase"]).Should().Equal("attempt", "outcome");
        _records.Select(r => r.CorrelationId).Distinct().Should().ContainSingle();
        _records.Should().OnlyContain(r => r.Action == AuditedExport.AuditAction);
        _records[0].Outcome.Should().Be(AuditOutcome.Attempted);
        var outcome = _records[1];
        outcome.Outcome.Should().Be(AuditOutcome.Success);
        outcome.Metadata!["subject_reference"].Should().Be(Reference().For(Subject));
        outcome.Metadata["export_request_id"].Should().Be("export-1");
        outcome.Metadata["complete"].Should().Be("false");
        outcome.Metadata["store.conversations"].Should().Be("Included:2");
        outcome.Metadata["store.plans"].Should().Be("NotExportable:0");
    }

    [Fact]
    public async Task ExportAsync_Succeeds_NoRecordAndNoLogLineCarriesTheSubjectOrTheirData()
    {
        _export.Setup(e => e.ExportOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Copy());

        await Create().ExportAsync(Subject, ErasureActor.DataSubject, "ExportMyDataCommand", CancellationToken.None);

        AllRecordedText().Should().NotContain(t => t.Contains(Subject, StringComparison.OrdinalIgnoreCase) || t.Contains(Content));
        _logger.Messages.Should().NotContain(m => m.Contains(Subject, StringComparison.OrdinalIgnoreCase) || m.Contains(Content));
    }

    [Fact]
    public async Task ExportAsync_Fails_RecordsFailureAndReturnsAScrubbedMessage_LoggingTheTypeButNotTheMessage()
    {
        _export.Setup(e => e.ExportOwnerAsync(Subject, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Server=db;Password=hunter2 for {Subject}"));

        var result = await Create().ExportAsync(Subject, ErasureActor.DataSubject, "ExportMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Equal(AuditedExport.FailedMessage);
        _records.Last().Outcome.Should().Be(AuditOutcome.Failure);
        _records.Last().FailureReason.Should().Be(nameof(InvalidOperationException));
        AllRecordedText().Should().NotContain(t => t.Contains("hunter2") || t.Contains(Subject));
        _logger.Exceptions.Should().ContainSingle().Which.ToString()
            .Should().Contain(typeof(InvalidOperationException).FullName).And.NotContain("hunter2").And.NotContain(Subject);
    }

    [Fact]
    public async Task ExportAsync_CallerCancels_StillRecordsTheOutcome()
    {
        _export.Setup(e => e.ExportOwnerAsync(Subject, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var act = () => Create().ExportAsync(Subject, ErasureActor.DataSubject, "ExportMyDataCommand", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _records.Last().FailureReason.Should().Be("cancelled");
    }

    [Fact]
    public async Task ExportAsync_AuditSinkThrows_TheCopyIsStillMade()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        _export.Setup(e => e.ExportOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Copy());

        var result = await Create().ExportAsync(Subject, ErasureActor.DataSubject, "ExportMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a broken audit sink must not refuse a person their copy");
    }

    [Fact]
    public async Task RecordDeniedAsync_RecordsTheRefusalWithoutReadingAnything()
    {
        await Create().RecordDeniedAsync(ErasureActor.DataSubject, "ExportMyDataCommand", "sign-in is bypassed");

        var denied = _records.Should().ContainSingle().Subject;
        denied.Outcome.Should().Be(AuditOutcome.Denied);
        denied.FailureReason.Should().Be("sign-in is bypassed");
        denied.Action.Should().Be(AuditedExport.AuditAction);
        _export.VerifyNoOtherCalls();
    }

    private IEnumerable<string> AllRecordedText() => _records.SelectMany(r =>
        new[] { r.ExecutorId ?? "", r.CorrelationId ?? "", r.FailureReason ?? "", r.RequestType }
            .Concat(r.Metadata?.Keys ?? [])
            .Concat(r.Metadata?.Values ?? []));
}
