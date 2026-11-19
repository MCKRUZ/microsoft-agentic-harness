using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.AI.Common.Services;
using Domain.AI.WorkMemory;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.WorkMemory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.WorkMemory;

public sealed class InMemoryWorkEpisodeStoreTests
{
    private readonly AmbientRequestScope _ambient = new();
    private readonly InMemoryWorkEpisodeStore _sut;

    public InMemoryWorkEpisodeStoreTests() => _sut = new InMemoryWorkEpisodeStore(_ambient);

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

    /// <summary>
    /// #968: an episode now names its owner, so owner erasure must remove the person's episodes, in every tenant, and
    /// nobody else's, including the episodes saved with no caller in flight.
    /// </summary>
    [Fact]
    public async Task EraseSubject_RemovesOnlyThatOwnersEpisodes_InEveryTenant()
    {
        var mineHere = await SaveAsAsync("user-a", "tenant-1");
        var mineThere = await SaveAsAsync("user-a", "tenant-2");
        var theirs = await SaveAsAsync("user-b", "tenant-1");
        var nobodys = BuildEpisode();
        await _sut.SaveAsync(nobodys, CancellationToken.None);

        var removed = await _sut.EraseSubjectAsync(new ErasureSubject("USER-A", []), CancellationToken.None);

        removed.Should().Be(2);
        (await _sut.GetAsync(mineHere.EpisodeId, CancellationToken.None)).Value.Should().BeNull();
        (await _sut.GetAsync(mineThere.EpisodeId, CancellationToken.None)).Value.Should().BeNull();
        (await _sut.GetAsync(theirs.EpisodeId, CancellationToken.None)).Value.Should().NotBeNull();
        (await _sut.GetAsync(nobodys.EpisodeId, CancellationToken.None)).Value.Should().NotBeNull();
    }

    /// <summary>#968: the owner's copy of their data includes their episodes and nobody else's.</summary>
    [Fact]
    public async Task ExportSubject_ReturnsOnlyThatOwnersEpisodes()
    {
        var mine = await SaveAsAsync("user-a", "tenant-1");
        await SaveAsAsync("user-b", "tenant-1");
        await _sut.SaveAsync(BuildEpisode(), CancellationToken.None);

        var records = await _sut.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Should().ContainSingle().Which.GetProperty("episodeId").GetGuid().Should().Be(mine.EpisodeId);
    }

    private async Task<WorkEpisode> SaveAsAsync(string userId, string tenantId)
    {
        var episode = BuildEpisode();
        await using var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IKnowledgeScope>(k => k.UserId == userId && k.TenantId == tenantId))
            .BuildServiceProvider();
        using (_ambient.BeginScope(services))
            await _sut.SaveAsync(episode, CancellationToken.None);
        return episode;
    }
}
