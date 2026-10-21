using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// The coordinator's order and failure rules (#900): conversation ids are read before anything is deleted, the
/// conversations go only after everything reached through them, and one failing store neither stops the rest nor lets
/// the receipt claim more than happened.
/// </summary>
public sealed class PersonalDataErasureCoordinatorTests
{
    private const string Owner = "Owner-1";
    private readonly Mock<IErasureOrchestrator> _graph = new();
    private readonly Mock<IConversationStore> _conversations = new();
    private readonly List<string> _calls = [];

    public PersonalDataErasureCoordinatorTests()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Full));
        // Listed once before anything is deleted, and once afterwards to find conversations started meanwhile.
        _conversations.SetupSequence(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation("c-1"), Conversation("c-2")])
            .ReturnsAsync([]);
    }

    private static ErasureReceipt GraphReceipt(ErasureCompleteness completeness, string? reason = null) => new()
    {
        RequestId = "r",
        ScopeId = Owner,
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 1,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 0,
        Completeness = completeness,
        CompletenessReason = reason,
    };

    private static ConversationRecord Conversation(string id) =>
        new(id, "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

    private IPersonalDataEraser Eraser(string name, ErasureStage stage, int removed = 1, Exception? failure = null)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Returns<ErasureSubject, CancellationToken>((_, _) =>
            {
                _calls.Add(name);
                return failure is null ? Task.FromResult(removed) : Task.FromException<int>(failure);
            });
        return eraser.Object;
    }

    private PersonalDataErasureCoordinator Create(params IPersonalDataEraser?[] erasers) =>
        new(_graph.Object, erasers!, TimeProvider.System, NullLogger<PersonalDataErasureCoordinator>.Instance,
            _conversations.Object);

    [Fact]
    public async Task EveryStoreSucceeds_ReceiptIsFullAndListsEachStore()
    {
        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, removed: 3),
                Eraser("conversations", ErasureStage.Conversations, removed: 2),
                Eraser("ledger", ErasureStage.AfterConversations, removed: 4))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        receipt.CompletenessReason.Should().BeNull();
        receipt.Stores.Should().Equal(
            StoreErasureOutcome.Erased("spills", 3),
            StoreErasureOutcome.Erased("conversations", 2),
            StoreErasureOutcome.Erased("ledger", 4));
        receipt.NodesDeleted.Should().Be(1, "the graph's own counts are kept on the combined receipt");
    }

    [Fact]
    public async Task StagesRunInOrder_RegardlessOfRegistrationOrder()
    {
        await Create(
                Eraser("ledger", ErasureStage.AfterConversations),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations", "ledger");
    }

    [Fact]
    public async Task ConversationIds_AreReadBeforeAnythingIsErased_AndHandedToEveryStore()
    {
        ErasureSubject? seen = null;
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns("spills");
        eraser.SetupGet(e => e.ErasureStage).Returns(ErasureStage.BeforeConversations);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Callback<ErasureSubject, CancellationToken>((s, _) => seen = s)
            .ReturnsAsync(0);

        await Create(eraser.Object).EraseOwnerAsync(Owner, CancellationToken.None);

        seen!.ConversationIds.Should().Equal("c-1", "c-2");
        seen.OwnerId.Should().Be(Owner);
        seen.CanonicalOwnerId.Should().Be("owner-1");
    }

    [Fact]
    public async Task AStoreReachedThroughConversationsFails_ConversationsAndLedgerAreHeldBack()
    {
        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, failure: new IOException("C:\\secret\\path locked")),
                Eraser("plans", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "plans");
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Select(s => (s.Store, s.Status)).Should().Equal(
            ("spills", StoreErasureStatus.Failed),
            ("plans", StoreErasureStatus.Erased),
            ("conversations", StoreErasureStatus.Skipped),
            ("ledger", StoreErasureStatus.Skipped));
        receipt.CompletenessReason.Should().NotContain("secret", "receipts are returned to the caller; exception text is not");
    }

    [Fact]
    public async Task TheConversationStoreFails_TheLedgerIsHeldBack()
    {
        var receipt = await Create(
                Eraser("conversations", ErasureStage.Conversations, failure: new InvalidOperationException()),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("conversations");
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task NoConversationEraserButConversationsExist_TheLedgerIsHeldBack()
    {
        // A host whose conversation store cannot erase leaves the conversations standing; removing their ledger rows
        // would let a once-only tool run again in them.
        var receipt = await Create(Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().BeEmpty();
        receipt.Stores.Single().Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task NoConversationsAtAll_TheLedgerStillRuns()
    {
        _conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Create(Eraser("ledger", ErasureStage.AfterConversations)).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("ledger");
    }

    [Fact]
    public async Task TheGraphThrows_OtherStoresStillRun_AndTheReceiptSaysPartial()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("neo4j://user:pass@host"));

        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations");
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores[0].Should().Be(new StoreErasureOutcome(
            "knowledge graph", StoreErasureStatus.Failed, 0, "the store could not be erased; the failure has been logged"));
        receipt.CompletenessReason.Should().NotContain("pass");
    }

    [Fact]
    public async Task TheGraphIsPartial_TheCombinedReceiptIsPartialAndKeepsItsReason()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Partial, "remote memory could not be purged"));

        var receipt = await Create(Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.CompletenessReason.Should().Contain("remote memory could not be purged");
    }

    [Fact]
    public async Task ListingConversationsFails_NothingIsErased()
    {
        _conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());
        var coordinator = Create(Eraser("spills", ErasureStage.BeforeConversations));

        var act = () => coordinator.EraseOwnerAsync(Owner, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _calls.Should().BeEmpty();
        _graph.Verify(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOwner_IsRefused_NeverAWildcard(string owner)
    {
        var act = () => Create(Eraser("spills", ErasureStage.BeforeConversations)).EraseOwnerAsync(owner, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AConfiguredStoreThatCannotErase_IsOnTheReceipt_AndDoesNotHoldTheConversationsBack()
    {
        var receipt = await Create(
                new NonErasableStore("CustomVectorStore", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().Contain(new StoreErasureOutcome(
            "CustomVectorStore", StoreErasureStatus.NotSupported, 0, NonErasableStore.Reason));
        _calls.Should().ContainSingle().Which.Should().Be(
            "conversations", "a retry would not change it, so holding the conversations back gains nothing");
    }

    [Fact]
    public async Task AConversationStoreThatCannotErase_HoldsBackTheStageAfterIt()
    {
        var receipt = await Create(
                new NonErasableStore("CustomConversationStore", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().BeEmpty();
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task AConversationStartedDuringErasure_IsReportedNotClaimedErased()
    {
        _conversations.SetupSequence(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation("c-1")])
            .ReturnsAsync([Conversation("c-new")]);

        var receipt = await Create(Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().Contain(s => s.Store == "conversations started during erasure"
            && s.Status == StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task TheCallerGoingAway_DoesNotInterruptTheConversationsOrTheStageAfterThem()
    {
        using var caller = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        IPersonalDataEraser Capturing(string name, ErasureStage stage, Action? onRun = null)
        {
            var eraser = new Mock<IPersonalDataEraser>();
            eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
            eraser.SetupGet(e => e.ErasureStage).Returns(stage);
            eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
                .Callback<ErasureSubject, CancellationToken>((_, token) => { tokens.Add(token); onRun?.Invoke(); })
                .ReturnsAsync(0);
            return eraser.Object;
        }

        await Create(
                Capturing("spills", ErasureStage.BeforeConversations, onRun: caller.Cancel),
                Capturing("conversations", ErasureStage.Conversations),
                Capturing("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, caller.Token);

        tokens.Should().HaveCount(3);
        tokens.Skip(1).Should().OnlyContain(t => !t.CanBeCanceled,
            "once conversations start being deleted, stopping halfway would orphan what depends on them");
    }

    [Fact]
    public async Task AForwardedRegistrationThatCannotErase_IsSkipped()
    {
        // AddPersonalDataEraser yields null when the host's store does not implement the interface.
        var receipt = await Create(null, Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle().Which.Store.Should().Be("spills");
    }
}
