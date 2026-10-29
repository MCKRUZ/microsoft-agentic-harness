using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Models;
using FluentAssertions;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// The document age limit retries a <c>Partial</c> removal every sweep (#998). The compliance trail is kept for good, so a
/// retry is recorded only when it ends differently from the last recorded attempt, and then as a retry of the first.
/// </summary>
public sealed class AuditedDocumentRemovalRetryTests
{
    private const string DocumentId = "0123456789ABCDEF";

    private readonly Mock<IErasureOrchestrator> _orchestrator = new();
    private readonly Mock<IAuditSink> _sink = new();
    private readonly Mock<IComplianceAuditTrail> _trail = new();
    private readonly List<AuditEntry> _kept = [];
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
    private readonly DocumentRemovalRetries _retries = new();
    private readonly DateTimeOffset _load = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public AuditedDocumentRemovalRetryTests()
    {
        _sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        _trail.Setup(t => t.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<ErasureActor>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, ErasureActor, CancellationToken>((e, _, _) => _kept.Add(e))
            .ReturnsAsync(Result.Success());
    }

    private AuditedDocumentRemoval Create() => new(
        _orchestrator.Object, Mock.Of<IDocumentLoadLedger>(), _sink.Object, _trail.Object, TestHostEnvironment.Instance, _clock,
        new CapturingLogger<AuditedDocumentRemoval>());

    private static ErasureReceipt Partial(int vectorsRemoved = 0, string reason = "vector store: unreachable") => new()
    {
        RequestId = Guid.NewGuid().ToString(),
        ScopeId = "document",
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 0,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = vectorsRemoved,
        Completeness = ErasureCompleteness.Partial,
        CompletenessReason = reason,
    };

    private void Returns(params ErasureReceipt[] receipts)
    {
        var queue = new Queue<ErasureReceipt>(receipts);
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Dequeue());
    }

    private Task<Result<ErasureReceipt>> RetainAsync(DateTimeOffset? load = null, DocumentRemovalRetries? retries = null) =>
        Create().RemoveAsync(DocumentId, ErasureActor.Retention, CancellationToken.None, load ?? _load, retries ?? _retries);

    private string[] Phases() => _kept.Select(e => e.Metadata!["phase"]).ToArray();

    [Fact]
    public async Task RemoveAsync_RetriesThatEndAsTheLastDid_AddNothingToTheTrail()
    {
        Returns(Partial(), Partial(), Partial());

        await RetainAsync();
        await RetainAsync();
        await RetainAsync();

        _orchestrator.Verify(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()), Times.Exactly(3),
            "control: every retry still removes");
        Phases().Should().Equal(["attempt", "outcome"], "a trail kept for good grows with requests, not with sweeps");
    }

    [Fact]
    public async Task RemoveAsync_ARetryThatEndsFull_IsRecordedAsARetryOfTheFirstAttempt()
    {
        Returns(Partial(), Partial(), Partial() with { Completeness = ErasureCompleteness.Full, CompletenessReason = null });

        await RetainAsync();
        _clock.Advance(TimeSpan.FromHours(6));
        await RetainAsync();
        _clock.Advance(TimeSpan.FromHours(6));
        await RetainAsync();

        Phases().Should().Equal("attempt", "outcome", "attempt", "outcome");
        var first = _kept[0];
        _kept.Skip(2).Should().OnlyContain(e => e.Metadata![DocumentRemovalRetries.RetryOfKey] == first.CorrelationId,
            "an auditor can tie the retry that finished the job to the request that began it");
        _kept[2].Outcome.Should().Be(AuditOutcome.Attempted);
        _kept[2].CorrelationId.Should().Be(_kept[3].CorrelationId).And.NotBe(first.CorrelationId);
        _kept[2].Timestamp.Should().Be(_kept[0].Timestamp.AddHours(12), "the deferred attempt keeps the time it began");
        _kept[3].Metadata!["completeness"].Should().Be(nameof(ErasureCompleteness.Full));
        _retries.Count.Should().Be(0, "a document fully removed is no longer being retried");
    }

    [Fact]
    public async Task RemoveAsync_ARetryThatRemovedSomething_IsRecordedThoughStillPartial()
    {
        Returns(Partial(), Partial(vectorsRemoved: 2));

        await RetainAsync();
        await RetainAsync();

        Phases().Should().Equal("attempt", "outcome", "attempt", "outcome");
        _kept[3].Metadata!["graph.vector_embeddings"].Should().Be("2", "data deleted by a retry is always on the record");
    }

    [Fact]
    public async Task RemoveAsync_ARetryWithAnotherStoreStillFailing_IsRecorded()
    {
        Returns(Partial(), Partial(reason: "full-text store: unreachable"));

        await RetainAsync();
        await RetainAsync();

        Phases().Should().HaveCount(4);
    }

    [Fact]
    public async Task RemoveAsync_RetriesThatFailTheSameWay_AreRecordedOnce()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        await RetainAsync();
        await RetainAsync();

        _kept.Select(e => e.Outcome).Should().Equal(AuditOutcome.Attempted, AuditOutcome.Failure);
    }

    [Fact]
    public async Task RemoveAsync_TheDocumentLoadedAgainSince_IsANewRequestAndRecordedInFull()
    {
        Returns(Partial(), Partial());

        await RetainAsync(_load);
        await RetainAsync(_load.AddYears(1));

        Phases().Should().Equal("attempt", "outcome", "attempt", "outcome");
        _kept[2].Metadata!.Should().NotContainKey(DocumentRemovalRetries.RetryOfKey);
    }

    [Fact]
    public async Task RemoveAsync_WithoutRetries_RecordsEveryAttempt()
    {
        Returns(Partial(), Partial());

        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);
        await Create().RemoveAsync(DocumentId, ErasureActor.Operator("operator-1"), CancellationToken.None);

        Phases().Should().HaveCount(4, "an operator's request is a request every time");
    }

    [Fact]
    public async Task RemoveAsync_ADocumentTheAgeLimitStoppedListing_IsRecordedInFullWhenItComesBack()
    {
        Returns(Partial(), Partial());

        await RetainAsync();
        _retries.KeepOnly([]);
        await RetainAsync();

        Phases().Should().HaveCount(4, "a document no longer listed was removed or reloaded, so its history is forgotten");
        _retries.Count.Should().Be(1);
    }

    [Fact]
    public async Task RemoveAsync_ARetryWhoseStoresEndedDifferently_IsRecorded()
    {
        var failing = Partial() with { Stores = [new StoreErasureOutcome("vector store", StoreErasureStatus.Failed, 0, "down")] };
        Returns(failing, failing, failing with { Stores = [new StoreErasureOutcome("vector store", StoreErasureStatus.Skipped, 0, "down")] });

        await RetainAsync();
        await RetainAsync();
        await RetainAsync();

        Phases().Should().HaveCount(4, "the same failing store is the same outcome; a store that ended another way is not");
    }

    [Fact]
    public async Task RemoveAsync_ARetryWhereAStoreRemovedItems_IsRecorded()
    {
        Returns(Partial(), Partial() with { Stores = [StoreErasureOutcome.Erased("cross-session memory", 3)] });

        await RetainAsync();
        await RetainAsync();

        Phases().Should().HaveCount(4);
    }
}
