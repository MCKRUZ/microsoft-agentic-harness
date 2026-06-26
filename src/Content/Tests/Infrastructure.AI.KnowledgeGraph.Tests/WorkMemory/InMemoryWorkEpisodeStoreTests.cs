using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.WorkMemory;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.WorkMemory;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.WorkMemory;

public sealed class InMemoryWorkEpisodeStoreTests
{
    private readonly InMemoryWorkEpisodeStore _sut = new();

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
        UserMessage = "task",
        ResponseSummary = "result",
        Outcome = outcome,
        InputTokens = 10,
        OutputTokens = 5,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SaveThenGet_RoundTripsEpisode()
    {
        var episode = BuildEpisode();

        (await _sut.SaveAsync(episode, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var fetched = await _sut.GetAsync(episode.EpisodeId, CancellationToken.None);

        fetched.IsSuccess.Should().BeTrue();
        fetched.Value.Should().Be(episode);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsSuccessWithNull()
    {
        var fetched = await _sut.GetAsync(Guid.NewGuid(), CancellationToken.None);

        fetched.IsSuccess.Should().BeTrue();
        fetched.Value.Should().BeNull();
    }

    [Fact]
    public async Task Search_FiltersByConversationOutcomeAndTime()
    {
        var now = DateTimeOffset.UtcNow;
        await _sut.SaveAsync(BuildEpisode(conversationId: "a", outcome: EpisodeOutcome.Success, createdAt: now), CancellationToken.None);
        await _sut.SaveAsync(BuildEpisode(conversationId: "a", outcome: EpisodeOutcome.Failure, createdAt: now), CancellationToken.None);
        await _sut.SaveAsync(BuildEpisode(conversationId: "b", outcome: EpisodeOutcome.Failure, createdAt: now), CancellationToken.None);

        var byConversation = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { ConversationId = "a" }, CancellationToken.None);
        byConversation.Value.Should().HaveCount(2);

        var failures = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { Outcome = EpisodeOutcome.Failure }, CancellationToken.None);
        failures.Value.Should().HaveCount(2);

        var afterCutoff = await _sut.SearchAsync(
            new WorkEpisodeSearchCriteria { CreatedAfter = now.AddMinutes(1) }, CancellationToken.None);
        afterCutoff.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_OrdersNewestFirstAndHonorsLimit()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var older = BuildEpisode(turnNumber: 1, createdAt: baseTime);
        var newer = BuildEpisode(turnNumber: 2, createdAt: baseTime.AddMinutes(5));
        await _sut.SaveAsync(older, CancellationToken.None);
        await _sut.SaveAsync(newer, CancellationToken.None);

        var result = await _sut.SearchAsync(new WorkEpisodeSearchCriteria { Limit = 1 }, CancellationToken.None);

        result.Value.Should().ContainSingle().Which.EpisodeId.Should().Be(newer.EpisodeId);
    }
}
