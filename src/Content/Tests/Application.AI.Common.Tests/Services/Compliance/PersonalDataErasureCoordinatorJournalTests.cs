using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// The erasure journal's part in a request (#907). Once a request deletes the conversations and plans, the journal is the
/// only way back to what they led to, so: it is merged into every request, written before any lead is deleted (and no
/// lead is deleted if it cannot be), and cleared only when every store that reads it succeeded, and only for its own rows.
/// </summary>
public sealed class PersonalDataErasureCoordinatorJournalTests
{
    private const string Owner = "Owner-1";
    private readonly Mock<IErasureOrchestrator> _graph = new();
    private readonly Mock<IConversationStore> _store = new();
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, ErasureSubject> _seen = [];
    private readonly FakeJournal _journal;

    public PersonalDataErasureCoordinatorJournalTests()
    {
        _journal = new FakeJournal(_calls);
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErasureReceipt
            {
                RequestId = "r",
                ScopeId = Owner,
                RequestedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch,
                NodesDeleted = 0,
                EdgesDeleted = 0,
                FeedbackWeightsDeleted = 0,
                VectorEmbeddingsDeleted = 0,
            });
        _store.As<IConversationIdSource>()
            .Setup(s => s.ListConversationIdsAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationIdListing(["c-now"], 0));
    }

    private IPersonalDataEraser Eraser(string name, ErasureStage stage, Exception? failure = null)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.SetupGet(e => e.ErasureReach).Returns(ErasureReach.NamedConversations);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Returns<ErasureSubject, CancellationToken>((s, _) =>
            {
                _calls.Add(name);
                _seen[name] = s;
                return failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);
            });
        return eraser.Object;
    }

    private PersonalDataErasureCoordinator Create(params IPersonalDataEraser[] erasers) =>
        new(_graph.Object, erasers, [], new AmbientRequestScope(), TimeProvider.System,
            NullLogger<PersonalDataErasureCoordinator>.Instance, _store.Object, journal: _journal);

    private IPersonalDataEraser[] AllStages(Exception? ledgerFailure = null) =>
    [
        Eraser("spills", ErasureStage.BeforeConversations),
        Eraser("conversations", ErasureStage.Conversations),
        Eraser("plans", ErasureStage.PlannedTasks),
        Eraser("ledger", ErasureStage.AfterConversations, ledgerFailure),
    ];

    [Fact]
    public async Task WhatAnEarlierRequestRecorded_IsMergedIntoThisOne_ForEveryStore()
    {
        _journal.Stored = new ErasureJournalEntries(
            ["c-gone"], [], new PlanRunTraces(["plan-gone:step"], ["job-gone"], ["planrun:plan-gone"]));

        await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _seen["spills"].ConversationIds.Should().BeEquivalentTo(["c-now", "c-gone"]);
        _seen["spills"].PlanRuns.ToolResultScopeIds.Should().Equal("job-gone");
        _seen["ledger"].AllConversationIds.Should().BeEquivalentTo(["c-now", "c-gone", "plan-gone:step"],
            "the stage after is what a failed earlier request could not finish");
        _seen["ledger"].PlanRuns.RunBudgetKeys.Should().Equal("planrun:plan-gone");
    }

    [Fact]
    public async Task TheIds_AreRecordedAfterTheFirstStage_AndBeforeAnyLeadIsDeleted()
    {
        await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().ContainInOrder("begin", "spills", "record", "conversations", "plans", "ledger", "end");
        _journal.Recorded!.ConversationIds.Should().Equal("c-now");
    }

    [Fact]
    public async Task TheJournalCannotBeWritten_NoLeadIsDeleted_AndTheReceiptSaysSo()
    {
        _journal.RecordFailure = new IOException("disk full");

        var receipt = await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("begin", "spills", "record", "end");
        receipt.Stores.Single(s => s.Store == "erasure journal").Status.Should().Be(StoreErasureStatus.Failed);
        receipt.Stores.Where(s => s.Store is "conversations" or "plans" or "ledger")
            .Should().OnlyContain(s => s.Status == StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task EveryStoreSucceeds_ThisRequestsRowsAreCleared()
    {
        await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.TakeLast(2).Should().Equal("clear", "end");
        _journal.ClearedRequest.Should().Be(_journal.RecordedRequest, "a request clears only what it recorded");
    }

    [Fact]
    public async Task AStoreThatCannotEraseAtAll_DoesNotKeepTheRows()
    {
        // A retry will not make it erase, so keeping the rows for it would keep the person's ids for nothing.
        await Create([.. AllStages(), new NonErasableStore("audit copies", ErasureStage.BeforeConversations)])
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.TakeLast(2).Should().Equal("clear", "end");
    }

    [Fact]
    public async Task AStoreAfterTheLeadsFails_TheRowsAreKept_SoTheNextRequestCanFinish()
    {
        // The case the journal exists for: the conversations are gone, so nothing else leads to the ledger's rows.
        var receipt = await Create(AllStages(new InvalidOperationException())).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().NotContain("clear");
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Failed);
        receipt.Stores.Single(s => s.Store == "erasure journal entries").Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry,
            "the person's ids stay in the journal, and the receipt says so");
    }

    [Fact]
    public async Task EveryStoreSucceeds_TheReceiptDoesNotMentionTheJournal()
    {
        var receipt = await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().NotContain(s => s.Store == "erasure journal entries");
    }

    [Fact]
    public async Task UnreadableConversations_KeepTheRows()
    {
        _store.As<IConversationIdSource>()
            .Setup(s => s.ListConversationIdsAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationIdListing(["c-now"], 1));

        await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().NotContain("clear");
    }

    [Fact]
    public async Task AReservedConversation_IsRecordedAsWithheld_NotAsAConversationToErase()
    {
        _store.As<IConversationIdSource>()
            .Setup(s => s.ListConversationIdsAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationIdListing(["c-now", "run:9"], 0));

        await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _journal.Recorded!.ConversationIds.Should().Equal("c-now");
        _journal.Recorded.WithheldConversationIds.Should().Equal("run:9");
    }

    [Fact]
    public async Task AWithheldIdRecordedEarlier_StillMakesTheReceiptSaySo()
    {
        // The conversation itself is gone, so only the journal still knows its records were kept.
        _journal.Stored = new ErasureJournalEntries([], ["run:9"], PlanRunTraces.Empty);

        var receipt = await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().Contain(s => s.Store == "conversations with a reserved id");
        receipt.Stores.Should().Contain(s => s.Store == "erasure journal entries", "the withheld id stays in the journal until it ages out");
        _seen["spills"].AllConversationIds.Should().NotContain("run:9");
    }

    [Fact]
    public async Task TheJournalCannotBeRead_TheLeadsAreStillErased_AndTheReceiptSaysSo()
    {
        // What it holds stays recorded for the next request, so this one need not hold anything back.
        _journal.ReadFailure = new IOException("locked");

        var receipt = await Create(AllStages()).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Contain(["conversations", "plans", "ledger"]);
        receipt.Stores.Single(s => s.Store == "erasure journal (earlier requests)").Status.Should().Be(StoreErasureStatus.Failed);
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
    }

    private sealed class FakeJournal(List<string> calls) : IErasureJournal
    {
        public ErasureJournalEntries Stored { get; set; } = ErasureJournalEntries.Empty;
        public ErasureJournalEntries? Recorded { get; private set; }
        public string? RecordedRequest { get; private set; }
        public string? ClearedRequest { get; private set; }
        public Exception? RecordFailure { get; set; }
        public Exception? ReadFailure { get; set; }

        public Task<ErasureJournalEntries> ReadAsync(string ownerId, CancellationToken cancellationToken) =>
            ReadFailure is null ? Task.FromResult(Stored) : Task.FromException<ErasureJournalEntries>(ReadFailure);

        public Task RecordAsync(string ownerId, string requestId, ErasureJournalEntries entries, CancellationToken cancellationToken)
        {
            calls.Add("record");
            if (RecordFailure is not null)
                return Task.FromException(RecordFailure);
            Recorded = entries;
            RecordedRequest = requestId;
            return Task.CompletedTask;
        }

        public Task<bool> HoldsForAnotherOwnerAsync(string conversationId, string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IDisposable> BeginRequestAsync(string ownerId, CancellationToken cancellationToken)
        {
            calls.Add("begin");
            return Task.FromResult<IDisposable>(new Turn(calls));
        }

        private sealed class Turn(List<string> calls) : IDisposable
        {
            public void Dispose() => calls.Add("end");
        }

        public Task ClearAsync(string ownerId, string requestId, CancellationToken cancellationToken)
        {
            calls.Add("clear");
            ClearedRequest = requestId;
            return Task.CompletedTask;
        }
    }
}
