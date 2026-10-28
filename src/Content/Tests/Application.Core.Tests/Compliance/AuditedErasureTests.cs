using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services.Compliance;
using Application.Common.Interfaces.MediatR;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseMyData;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// The erasure audit record (#900): it proves the request happened, who asked, and what each store removed, and never
/// keeps the identity the request erased, in the record or in the log.
/// </summary>
public sealed class AuditedErasureTests
{
    private const string Subject = "Subject-Oid-7f3a";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<IPersonalDataErasure> _erasure = new();
    private readonly List<AuditEntry> _records = [];
    private readonly Mock<IAuditSink> _sink = new();
    private readonly List<(AuditEntry Entry, ErasureActor Actor)> _kept = [];
    private readonly Mock<IComplianceAuditTrail> _trail = new();
    private readonly CapturingLogger<AuditedErasure> _logger = new();
    private readonly ErasureConfig _config = new() { SubjectReferenceKey = Key };

    public AuditedErasureTests()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, ErasureActor, CancellationToken>((e, a, _) => _kept.Add((e, a)))
            .ReturnsAsync(Result.Success());
    }

    private HmacErasureSubjectReference Reference() =>
        new(Mock.Of<IOptionsMonitor<ErasureConfig>>(m => m.CurrentValue == _config));

    private AuditedErasure Create() => new(_erasure.Object, Reference(), _sink.Object, _trail.Object, TimeProvider.System, _logger);

    private static ErasureReceipt Receipt() => new()
    {
        RequestId = "receipt-1",
        ScopeId = Subject,
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 3,
        EdgesDeleted = 2,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 4,
        Stores = [StoreErasureOutcome.Erased("conversations", 5)],
    };

    private string ExpectedReference() => Reference().For(Subject)!;

    public static TheoryData<IHasTimeout> ErasureCommands() => new()
    {
        new EraseMyDataCommand(),
        new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = "operator-1" },
    };

    /// <summary>
    /// Erasure first waits up to 30 seconds for the person's running planned tasks to stop (#907); under the pipeline's
    /// default limit the request would time out before reading a single store, handing the person an error in place of a
    /// receipt while the deletes carried on.
    /// </summary>
    [Theory]
    [MemberData(nameof(ErasureCommands))]
    public void ErasureCommand_RunsLongerThanTheDefaultLimitPlusTheRunningTaskWait(IHasTimeout command)
    {
        var defaultLimit = TimeSpan.FromSeconds(new AgentConfig().DefaultRequestTimeoutSec);
        var runningTaskWait = TimeSpan.FromSeconds(30);

        command.Timeout.Should().Be(AuditedErasure.RequestTimeout);
        AuditedErasure.RequestTimeout.Should().BeGreaterThan(defaultLimit + runningTaskWait);
    }

    [Fact]
    public async Task EraseAsync_Succeeds_RecordsAttemptAndOutcomeWithCountsAndReference()
    {
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _records.Select(r => r.Metadata!["phase"]).Should().Equal("attempt", "outcome");
        _records.Select(r => r.CorrelationId).Distinct().Should().ContainSingle("both phases pair up by correlation id");
        _records[0].Outcome.Should().Be(AuditOutcome.Attempted,
            "an erasure that dies part-way must not leave a record claiming it succeeded");
        var outcome = _records[1];
        outcome.Outcome.Should().Be(AuditOutcome.Success);
        outcome.ExecutorId.Should().Be(ErasureActor.DataSubjectExecutor);
        outcome.Metadata!["subject_reference"].Should().Be(ExpectedReference());
        outcome.Metadata["receipt_request_id"].Should().Be("receipt-1");
        outcome.Metadata["completeness"].Should().Be("Full");
        outcome.Metadata["graph.nodes"].Should().Be("3");
        outcome.Metadata["store.conversations"].Should().Be("Erased:5");
    }

    [Fact]
    public async Task EraseAsync_Succeeds_NoRecordAndNoLogLineCarriesTheSubjectsId()
    {
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        AllRecordedText().Should().NotContain(t => t.Contains(Subject, StringComparison.OrdinalIgnoreCase));
        _logger.Messages.Should().NotContain(m => m.Contains(Subject, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EraseAsync_ByOperator_RecordsTheOperatorAsTheExecutor()
    {
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().EraseAsync(Subject, ErasureActor.Operator("operator-9"), "EraseSubjectDataCommand", CancellationToken.None);

        _records.Should().OnlyContain(r => r.ExecutorId == "operator-9" && r.Metadata!["initiator"] == "operator");
        AllRecordedText().Should().NotContain(t => t.Contains(Subject, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EraseAsync_NoReferenceKey_RecordsNoReferenceAndStillErases()
    {
        _config.SubjectReferenceKey = string.Empty;
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue("erasure is a legal duty and never waits on audit configuration");
        _records.Should().OnlyContain(r => !r.Metadata!.ContainsKey("subject_reference"));
    }

    [Fact]
    public async Task EraseAsync_StoreThrows_RecordsFailureAndReturnsAScrubbedMessage()
    {
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Server=db;Password=hunter2 for {Subject}"));

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Equal(AuditedErasure.FailedMessage);
        var outcome = _records.Should().HaveCount(2).And.Subject.Last();
        outcome.Outcome.Should().Be(AuditOutcome.Failure);
        outcome.FailureReason.Should().Be(nameof(InvalidOperationException));
        AllRecordedText().Should().NotContain(t => t.Contains("hunter2") || t.Contains(Subject));
    }

    [Fact]
    public async Task EraseAsync_StoreThrows_TheLoggedExceptionKeepsItsTypeButNotItsMessage()
    {
        // #907: a store's message can name the person being erased, and pattern-based log redaction lets an id through.
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"ownerId eq '{Subject}'"));

        await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        var logged = _logger.Exceptions.Should().ContainSingle().Subject;
        logged.ToString().Should().Contain(typeof(InvalidOperationException).FullName).And.NotContain(Subject);
    }

    [Fact]
    public async Task EraseAsync_AuditSinkThrows_ErasureStillRuns()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a broken audit sink must not refuse a person their right to erasure");
        _erasure.Verify(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EraseAsync_ReferenceKeyUnusable_ErasesAndRecordsTheReferenceAsUnavailable()
    {
        // A key that validated at startup can be replaced by a bad one on reload; that must degrade the record, never the
        // erasure.
        _config.SubjectReferenceKey = "AAEC";
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _records.Should().HaveCount(2).And.OnlyContain(r =>
            r.Metadata!["subject_reference"] == AuditedErasure.ReferenceUnavailable);
    }

    [Fact]
    public async Task EraseAsync_CallerCancelsPartWay_StillRecordsTheOutcome()
    {
        using var cts = new CancellationTokenSource();
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var outcome = _records.Should().HaveCount(2).And.Subject.Last();
        outcome.Outcome.Should().Be(AuditOutcome.Failure);
        outcome.FailureReason.Should().Be("cancelled");
        _sink.Verify(s => s.RecordAsync(It.IsAny<AuditEntry>(), cts.Token), Times.Never,
            "the outcome must be written on a token the caller cannot cancel");
    }

    [Fact]
    public async Task RecordDeniedAsync_WritesADeniedOutcomeWithTheNamedSubjectByReferenceOnly()
    {
        await Create().RecordDeniedAsync(ErasureActor.Operator("operator-9"), "EraseSubjectDataCommand", "missing the erasure role", Subject);

        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be("missing the erasure role");
        record.Action.Should().Be(AuditedErasure.AuditAction);
        record.Metadata!["subject_reference"].Should().Be(ExpectedReference());
        AllRecordedText().Should().NotContain(t => t.Contains(Subject, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecordDeniedAsync_SubjectLongerThanAnyValidRequest_IsNotHashed()
    {
        await Create().RecordDeniedAsync(ErasureActor.Operator("operator-9"), "EraseSubjectDataCommand", "missing the erasure role",
            new string('x', 100_000));

        _records.Should().ContainSingle().Which.Metadata!["subject_reference"].Should().Be(AuditedErasure.ReferenceInvalid);
    }

    [Fact]
    public async Task EraseAsync_Succeeds_KeepsTheSameAttemptAndOutcomeInTheComplianceTrail()
    {
        // #969: the log line is deleted with the logs; the trail is the proof that outlives them.
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());
        var actor = ErasureActor.Operator("operator-1");

        await Create().EraseAsync(Subject, actor, "EraseSubjectDataCommand", CancellationToken.None);

        _kept.Select(k => k.Entry).Should().Equal(_records, "the trail keeps exactly the records the audit sink receives");
        _kept.Should().HaveCount(2).And.OnlyContain(k => k.Actor == actor, "the trail decides from the actor how to record them");
    }

    [Fact]
    public async Task EraseAsync_StoreThrows_KeepsTheFailureInTheComplianceTrail()
    {
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        _kept.Select(k => k.Entry.Outcome).Should().Equal(AuditOutcome.Attempted, AuditOutcome.Failure);
    }

    [Fact]
    public async Task EraseAsync_ComplianceTrailThrows_ErasesAndStillWritesTheAuditSink()
    {
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a broken compliance trail must not refuse a person their right to erasure");
        _records.Should().HaveCount(2, "the audit sink is written whatever happened to the trail");
        _logger.Logged(LogLevel.Error, "compliance audit trail").Should().BeTrue("a record the trail lost must not vanish silently");
    }

    [Fact]
    public async Task EraseAsync_ComplianceTrailRefusesTheRecord_LogsWhyAndStillErases()
    {
        // A busy chain or a full disk comes back as a failed result, not an exception; it must still be logged.
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("the audit chain is busy"));
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _logger.Logged(LogLevel.Error, "the audit chain is busy").Should().BeTrue("a record the trail refused must not vanish silently");
        _records.Should().HaveCount(2);
    }

    [Fact]
    public async Task EraseAsync_ComplianceTrailReturnsNoResult_LogsTheBrokenContractAndStillErases()
    {
        // A replacement trail that breaks the contract must read as that in the log, not as an anonymous null reference.
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result)null!);
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _logger.Logged(LogLevel.Error, "returned no result").Should().BeTrue();
    }

    [Fact]
    public async Task EraseAsync_AuditSinkThrows_StillKeepsBothRecordsInTheComplianceTrail()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        _erasure.Setup(e => e.EraseOwnerAsync(Subject, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().EraseAsync(Subject, ErasureActor.DataSubject, "EraseMyDataCommand", CancellationToken.None);

        _kept.Should().HaveCount(2, "the trail is written whatever happened to the audit sink");
    }

    [Fact]
    public async Task RecordDeniedAsync_KeepsTheRefusalInTheComplianceTrail()
    {
        await Create().RecordDeniedAsync(ErasureActor.UnidentifiedOperator, "EraseSubjectDataCommand", "no identity", Subject);

        var kept = _kept.Should().ContainSingle().Subject;
        kept.Entry.Outcome.Should().Be(AuditOutcome.Denied);
        kept.Actor.Should().Be(ErasureActor.UnidentifiedOperator);
    }

    private IEnumerable<string> AllRecordedText() => _records.SelectMany(r =>
        new[] { r.ExecutorId ?? "", r.CorrelationId ?? "", r.FailureReason ?? "", r.RequestType }
            .Concat(r.Metadata?.Keys ?? [])
            .Concat(r.Metadata?.Values ?? []));
}
