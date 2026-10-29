using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// The document-removal audit record (#901): every request leaves a trace of who asked, which document and what each
/// store removed, including a request that fails or is abandoned part-way, and a store fault never reaches the caller.
/// </summary>
public sealed class AuditedDocumentRemovalTests
{
    private const string DocumentId = "0123456789ABCDEF";

    private readonly Mock<IErasureOrchestrator> _orchestrator = new();
    private readonly Mock<IDocumentLoadLedger> _ledger = new();
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
    private readonly List<AuditEntry> _records = [];
    private readonly Mock<IAuditSink> _sink = new();
    private readonly List<(AuditEntry Entry, ErasureActor Actor)> _kept = [];
    private readonly Mock<IComplianceAuditTrail> _trail = new();
    private readonly CapturingLogger<AuditedDocumentRemoval> _logger = new();

    public AuditedDocumentRemovalTests()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, ErasureActor, CancellationToken>((e, a, _) => _kept.Add((e, a)))
            .ReturnsAsync(Result.Success());
    }

    private AuditedDocumentRemoval Create() => new(_orchestrator.Object, _ledger.Object, _sink.Object, _trail.Object, TestHostEnvironment.Instance, _clock, _logger);

    [Fact]
    public async Task RemoveAsync_Complete_ForgetsTheLoadRecord_UpToWhenTheRemovalStarted()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        _ledger.Verify(l => l.ForgetAsync(DocumentId, _clock.GetUtcNow(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_TheReceiptAndEveryRecordNameTheHostTheRemovalCovered()
    {
        // #1003: a removal reaches only this process's document indexes and graph.
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        result.Value!.Host.Should().Be(TestHostEnvironment.Name);
        _records.Should().HaveCount(2).And.OnlyContain(r => r.Metadata!["host"] == TestHostEnvironment.Name);
    }

    [Fact]
    public async Task RemoveAsync_WithTheLoadTheCallerJudged_ForgetsOnlyUpToIt()
    {
        // The age limit passes the load time it judged old, so a load recorded since keeps its record.
        var judged = _clock.GetUtcNow().AddYears(-4);
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None, judged);

        _ledger.Verify(l => l.ForgetAsync(DocumentId, judged, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_Partial_KeepsTheLoadRecord_SoTheAgeLimitTriesAgain()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Receipt() with { Completeness = ErasureCompleteness.Partial });

        await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        _ledger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveAsync_LoadRecordCannotBeForgotten_StillSucceeds()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());
        _ledger.Setup(l => l.ForgetAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database locked"));

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the document is gone; a stale record only finds nothing later");
    }

    private static ErasureReceipt Receipt(IReadOnlyList<string>? unmatched = null) => new()
    {
        RequestId = "receipt-1",
        ScopeId = "document",
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 3,
        EdgesDeleted = 2,
        FeedbackWeightsDeleted = 1,
        VectorEmbeddingsDeleted = 4,
        Bm25DocumentsDeleted = 5,
        UnmatchedDocumentIds = unmatched ?? [],
    };

    [Fact]
    public async Task RemoveAsync_Succeeds_RecordsAttemptAndOutcomeWithTheDocumentAndCounts()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _records.Select(r => r.Metadata!["phase"]).Should().Equal("attempt", "outcome");
        _records.Select(r => r.CorrelationId).Distinct().Should().ContainSingle("both phases pair up by correlation id");
        _records.Should().OnlyContain(r =>
            r.Action == AuditedDocumentRemoval.AuditAction && r.ExecutorId == "operator-1"
            && r.Metadata!["document_id"] == DocumentId && r.Metadata["initiator"] == "operator");
        _records[0].Outcome.Should().Be(AuditOutcome.Attempted,
            "a removal that dies part-way must not leave a record claiming it succeeded");
        var outcome = _records[1];
        outcome.Outcome.Should().Be(AuditOutcome.Success);
        outcome.Metadata!["receipt_request_id"].Should().Be("receipt-1");
        outcome.Metadata["completeness"].Should().Be("Full");
        outcome.Metadata["matched"].Should().Be("true");
        outcome.Metadata["graph.nodes"].Should().Be("3");
        outcome.Metadata["graph.edges"].Should().Be("2");
        outcome.Metadata["graph.feedback_weights"].Should().Be("1");
        outcome.Metadata["graph.vector_embeddings"].Should().Be("4");
        outcome.Metadata["graph.bm25_documents"].Should().Be("5");
    }

    [Fact]
    public async Task RemoveAsync_AStoreLineOnTheReceipt_IsNamedInTheOutcomeRecord()
    {
        // A store an orchestrator lists on the receipt is a line of its own in the record, as it is on erasure.
        const string Index = "document index (Azure AI Search vectors)";
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Receipt() with
            {
                Stores = [new StoreErasureOutcome(Index, StoreErasureStatus.Failed, 0, "unreachable")],
                Completeness = ErasureCompleteness.Partial,
            });

        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        _records[1].Metadata!["store." + Index].Should().Be("Failed:0");
        _records[1].Metadata["completeness"].Should().Be("Partial");
    }

    [Fact]
    public async Task RemoveAsync_IdMatchedNothing_RecordsThatItMatchedNothing()
    {
        // A wrong id still yields a receipt; the record must say nothing was there, so no one mistakes it for a removal.
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Receipt(unmatched: [DocumentId]));

        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        _records[1].Metadata!["matched"].Should().Be("false");
    }

    [Fact]
    public async Task RemoveAsync_StoreFault_ReturnsTheFixedMessageAndRecordsTheFailureType()
    {
        // Store exceptions can carry connection strings and paths: the caller and the record get neither.
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Server=secret-host;Password=hunter2"));

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Equal(AuditedDocumentRemoval.FailedMessage);
        _records.Select(r => r.Outcome).Should().Equal(AuditOutcome.Attempted, AuditOutcome.Failure);
        _records[1].FailureReason.Should().Be(nameof(InvalidOperationException));
        _records.SelectMany(r => r.Metadata!.Values).Should().NotContain(v => v.Contains("hunter2"));
    }

    [Fact]
    public async Task RemoveAsync_Cancelled_RecordsTheFailureAndRethrows()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _records.Select(r => r.Outcome).Should().Equal(AuditOutcome.Attempted, AuditOutcome.Failure);
        _records[1].FailureReason.Should().Be("cancelled");
    }

    [Fact]
    public async Task RemoveAsync_AuditSinkBroken_StillRemovesTheDocument()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Throws(new IOException("disk full"));
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _orchestrator.Verify(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_CallerCancelsAfterStart_RecordsAreWrittenUncancelled()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((_, ct) => tokens.Add(ct))
            .Returns(ValueTask.CompletedTask);
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), cts.Token);

        tokens.Should().HaveCount(2).And.OnlyContain(t => !t.CanBeCanceled,
            "an abandoned request must still leave its outcome in the trail");
    }

    [Theory]
    [InlineData(DocumentId, DocumentId)]
    [InlineData("0123456789abcdef", AuditedDocumentRemoval.DocumentInvalid)]
    [InlineData("an arbitrary string of any length the caller sent", AuditedDocumentRemoval.DocumentInvalid)]
    [InlineData(null, AuditedDocumentRemoval.DocumentInvalid)]
    public async Task RecordDeniedAsync_RecordsTheDocumentOnlyWhenWellFormed(string? documentId, string recorded)
    {
        await Create().RecordDeniedAsync(ErasureActor.Operator("operator-1"), "missing the document-removal role", documentId);

        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be("missing the document-removal role");
        record.ExecutorId.Should().Be("operator-1");
        record.Metadata!["document_id"].Should().Be(recorded);
        _orchestrator.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("0123456789ABCDEF", true)]
    [InlineData("FFFFFFFFFFFFFFFF", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789ABCDE", false)]
    [InlineData("0123456789ABCDEF0", false)]
    [InlineData("0123456789ABCDEG", false)]
    [InlineData("0123456789ABCDEF\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDocumentId_AcceptsOnlySixteenUpperCaseHexDigits(string? documentId, bool expected) =>
        AuditedDocumentRemoval.IsDocumentId(documentId).Should().Be(expected);

    [Fact]
    public async Task RemoveAsync_Succeeds_KeepsTheSameAttemptAndOutcomeInTheComplianceTrail()
    {
        // #969: the log line is deleted with the logs; the trail is the proof that outlives them.
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        _kept.Select(k => k.Entry).Should().Equal(_records, "the trail keeps exactly the records the audit sink receives");
        _kept.Should().HaveCount(2).And.OnlyContain(k => k.Actor == ErasureActor.Retention);
    }

    [Fact]
    public async Task RemoveAsync_StoreFault_KeepsTheFailureInTheComplianceTrail()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        _kept.Select(k => k.Entry.Outcome).Should().Equal(AuditOutcome.Attempted, AuditOutcome.Failure);
    }

    [Fact]
    public async Task RemoveAsync_ComplianceTrailThrows_RemovesAndStillWritesTheAuditSink()
    {
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a broken compliance trail must not stop a removal");
        _records.Should().HaveCount(2, "the audit sink is written whatever happened to the trail");
        _logger.Logged(LogLevel.Error, "compliance audit trail").Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_ComplianceTrailRefusesTheRecord_LogsWhyAndRemoves()
    {
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("the audit chain is busy"));
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _logger.Logged(LogLevel.Error, "the audit chain is busy").Should().BeTrue("a record the trail refused must not vanish silently");
    }

    [Fact]
    public async Task RemoveAsync_AuditSinkThrows_StillKeepsBothRecordsInTheComplianceTrail()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        await Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None);

        _kept.Should().HaveCount(2, "the trail is written whatever happened to the audit sink");
    }

    [Fact]
    public async Task RecordDeniedAsync_KeepsTheRefusalInTheComplianceTrail()
    {
        await Create().RecordDeniedAsync(ErasureActor.UnidentifiedOperator, "no identity", DocumentId);

        _kept.Should().ContainSingle().Which.Entry.Outcome.Should().Be(AuditOutcome.Denied);
    }
}
