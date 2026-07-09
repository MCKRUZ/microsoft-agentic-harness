using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.WorkMemory;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Tests.TestSupport;
using Infrastructure.AI.KnowledgeGraph.WorkMemory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.WorkMemory;

public sealed class GraphWorkEpisodeStoreTests
{
    private readonly InMemoryGraphStore _graphStore;
    private readonly GraphWorkEpisodeStore _sut;

    public GraphWorkEpisodeStoreTests()
    {
        _graphStore = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _sut = new GraphWorkEpisodeStore(
            _graphStore, StubAmbientRequestScope.None(), Mock.Of<ILogger<GraphWorkEpisodeStore>>());
    }

    private static WorkEpisode BuildEpisode(
        Guid? id = null,
        string conversationId = "conv-1",
        int turnNumber = 1,
        EpisodeOutcome outcome = EpisodeOutcome.Success,
        DateTimeOffset? createdAt = null) => new()
    {
        EpisodeId = id ?? Guid.NewGuid(),
        AgentId = "agent-x",
        ConversationId = conversationId,
        TurnNumber = turnNumber,
        UserMessage = "the task",
        ResponseSummary = "the result",
        Outcome = outcome,
        InputTokens = 120,
        OutputTokens = 45,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SaveThenGet_RoundTripsAllFields()
    {
        var episode = BuildEpisode();

        (await _sut.SaveAsync(episode, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var fetched = (await _sut.GetAsync(episode.EpisodeId, CancellationToken.None)).Value;

        fetched.Should().NotBeNull();
        fetched!.EpisodeId.Should().Be(episode.EpisodeId);
        fetched.AgentId.Should().Be("agent-x");
        fetched.ConversationId.Should().Be("conv-1");
        fetched.TurnNumber.Should().Be(1);
        fetched.UserMessage.Should().Be("the task");
        fetched.ResponseSummary.Should().Be("the result");
        fetched.Outcome.Should().Be(EpisodeOutcome.Success);
        fetched.InputTokens.Should().Be(120);
        fetched.OutputTokens.Should().Be(45);
        fetched.CreatedAt.Should().Be(episode.CreatedAt);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsSuccessWithNull()
    {
        var result = await _sut.GetAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Search_ByConversation_ReturnsOnlyThatConversationViaIndex()
    {
        await _sut.SaveAsync(BuildEpisode(conversationId: "alpha", turnNumber: 1), CancellationToken.None);
        await _sut.SaveAsync(BuildEpisode(conversationId: "alpha", turnNumber: 2), CancellationToken.None);
        await _sut.SaveAsync(BuildEpisode(conversationId: "beta", turnNumber: 1), CancellationToken.None);

        var result = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { ConversationId = "alpha" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().OnlyContain(e => e.ConversationId == "alpha");
    }

    [Fact]
    public async Task Search_UnknownConversation_ReturnsEmpty()
    {
        await _sut.SaveAsync(BuildEpisode(conversationId: "alpha"), CancellationToken.None);

        var result = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { ConversationId = "missing" }, CancellationToken.None);

        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_NoConversation_ScansAllAndFiltersByOutcome()
    {
        await _sut.SaveAsync(BuildEpisode(conversationId: "a", outcome: EpisodeOutcome.Success), CancellationToken.None);
        await _sut.SaveAsync(BuildEpisode(conversationId: "b", outcome: EpisodeOutcome.Failure), CancellationToken.None);

        var failures = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { Outcome = EpisodeOutcome.Failure }, CancellationToken.None);

        failures.Value.Should().ContainSingle().Which.Outcome.Should().Be(EpisodeOutcome.Failure);
    }

    [Fact]
    public async Task Search_OrdersNewestFirstAndHonorsLimit()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var older = BuildEpisode(conversationId: "c", createdAt: baseTime);
        var newer = BuildEpisode(conversationId: "c", createdAt: baseTime.AddMinutes(5));
        await _sut.SaveAsync(older, CancellationToken.None);
        await _sut.SaveAsync(newer, CancellationToken.None);

        var result = await _sut.SearchAsync(
            new WorkEpisodeSearchCriteria { ConversationId = "c", Limit = 1 }, CancellationToken.None);

        result.Value.Should().ContainSingle().Which.EpisodeId.Should().Be(newer.EpisodeId);
    }

    [Fact]
    public async Task Save_UsesDeterministicLowercaseNodeId()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(BuildEpisode(id: id), CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"workepisode:{id}".ToLowerInvariant(), CancellationToken.None);
        node.Should().NotBeNull();
        node!.Type.Should().Be("WorkEpisode");
    }

    [Fact]
    public async Task Save_StampsCallerCanonicalOwner_SoOwnerScopedErasureCanFindIt()
    {
        // D3 owner-stamp: the compliance decorator leaves OwnerId writer-authoritative, so unless the
        // store stamps the caller's owner the node persists owner-less and owner-scoped right-to-erasure
        // (GetNodesByOwnerAsync) can never match it. Owner is canonicalized (trimmed/lowercased).
        var sut = new GraphWorkEpisodeStore(
            _graphStore, StubAmbientRequestScope.ForOwner("  User-42  "), Mock.Of<ILogger<GraphWorkEpisodeStore>>());
        var episode = BuildEpisode();

        await sut.SaveAsync(episode, CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"workepisode:{episode.EpisodeId}".ToLowerInvariant(), CancellationToken.None);
        node!.OwnerId.Should().Be("user-42", "the saved node must carry the caller's canonical owner");

        var owned = await _graphStore.GetNodesByOwnerAsync("user-42", CancellationToken.None);
        owned.Should().Contain(n => n.Id == node.Id,
            "owner-scoped erasure resolves nodes via GetNodesByOwnerAsync and must reach the work-memory node");
    }
}
