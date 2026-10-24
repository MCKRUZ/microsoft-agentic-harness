using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// How the coordinator reads the subject's conversations and logs failures (#907): through the store's id-only erasure
/// listing, which never skips a record it cannot read, and with every exception's message withheld from the log, because
/// a store's message can name the person being erased.
/// </summary>
public sealed class PersonalDataErasureCoordinatorListingTests
{
    private const string Owner = "Owner-1";
    private readonly Mock<IErasureOrchestrator> _graph = new();
    private readonly Mock<IConversationStore> _store = new();
    private readonly CapturingLogger _logger = new();
    private readonly List<ErasureSubject> _seen = [];

    public PersonalDataErasureCoordinatorListingTests()
    {
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
    }

    private Mock<IConversationIdSource> ListsIds(params ConversationIdListing[] listings)
    {
        var source = _store.As<IConversationIdSource>();
        var sequence = source.SetupSequence(s => s.ListConversationIdsAsync(Owner, It.IsAny<CancellationToken>()));
        foreach (var listing in listings)
            sequence = sequence.ReturnsAsync(listing);
        return source;
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
                _seen.Add(s);
                return failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);
            });
        return eraser.Object;
    }

    private PersonalDataErasureCoordinator Create(params IPersonalDataEraser[] erasers) =>
        new(_graph.Object, erasers, [], new AmbientRequestScope(), TimeProvider.System, _logger, _store.Object);

    [Fact]
    public async Task AStoreWithAnErasureListing_IsListedThroughIt_AndItsTranscriptListingIsNeverRead()
    {
        // #907: the transcript listing loads every message only to collect ids.
        ListsIds(new ConversationIdListing(["c-1", "c-2"], 0), ConversationIdListing.Empty);

        var receipt = await Create(Eraser("conversations", ErasureStage.Conversations)).EraseOwnerAsync(Owner, CancellationToken.None);

        _seen.Single().ConversationIds.Should().Equal("c-1", "c-2");
        _store.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task UnreadableConversations_AreAFailedLine_WhileEverythingReadableIsStillErased()
    {
        ListsIds(new ConversationIdListing(["c-1"], 2), ConversationIdListing.Empty);

        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _seen.Should().HaveCount(3, "an unreadable record must not hold back what the readable ones lead to");
        _seen.Should().OnlyContain(s => s.ConversationIds.SequenceEqual(new[] { "c-1" }));
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().ContainSingle(s => s.Store == "conversations that could not be read")
            .Which.Status.Should().Be(StoreErasureStatus.Failed);
    }

    [Fact]
    public async Task UnreadableConversations_HoldBackAStageAfterStoreThatErasesTheWholePerson()
    {
        // Security review: such a store cannot be narrowed to the readable conversations, so it would take an unreadable
        // conversation's records with nothing left that leads to them.
        ListsIds(new ConversationIdListing(["c-1"], 1), ConversationIdListing.Empty);
        var wholePerson = new Mock<IPersonalDataEraser>();
        wholePerson.SetupGet(e => e.ErasureStoreName).Returns("owner-keyed");
        wholePerson.SetupGet(e => e.ErasureStage).Returns(ErasureStage.AfterConversations);
        wholePerson.SetupGet(e => e.ErasureReach).Returns(ErasureReach.Owner);

        var receipt = await Create(Eraser("conversations", ErasureStage.Conversations), wholePerson.Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

        wholePerson.Verify(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()), Times.Never);
        receipt.Stores.Single(s => s.Store == "owner-keyed").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task AConversationStartedMeanwhile_IsFoundThroughTheErasureListingToo()
    {
        ListsIds(new ConversationIdListing(["c-1"], 0), new ConversationIdListing(["c-new"], 0));

        var receipt = await Create(Eraser("conversations", ErasureStage.Conversations)).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().Contain(s => s.Store == "conversations started during erasure");
        _store.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AStoreFailure_IsLoggedWithItsTypeAndStack_ButNeverItsMessage()
    {
        // A file store's IOException names a path built from a conversation id; an Azure request failure can echo the
        // owner filter. Pattern-based log redaction lets both through.
        ListsIds(new ConversationIdListing(["c-1"], 0), ConversationIdListing.Empty);
        var failure = new IOException(
            "Could not open C:\\data\\conversations\\c-1.json for Owner-1",
            new InvalidOperationException("ownerId eq 'Owner-1'"));

        await Create(Eraser("spills", ErasureStage.BeforeConversations, failure)).EraseOwnerAsync(Owner, CancellationToken.None);

        var logged = _logger.Exceptions.Should().ContainSingle().Subject;
        logged.Should().BeOfType<MessageWithheldException>();
        logged.ToString().Should().NotContain("Owner-1").And.NotContain("c-1.json")
            .And.Contain(typeof(IOException).FullName).And.Contain(typeof(InvalidOperationException).FullName);
    }

    private sealed class CapturingLogger : ILogger<PersonalDataErasureCoordinator>
    {
        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Exceptions.Add(exception);
        }
    }
}
