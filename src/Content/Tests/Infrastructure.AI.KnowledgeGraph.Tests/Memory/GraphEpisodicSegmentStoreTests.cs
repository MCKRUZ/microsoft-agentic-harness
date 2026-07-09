using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

/// <summary>
/// Tests for <see cref="GraphEpisodicSegmentStore"/> — round-trip persistence of raw episodic segments
/// and per-conversation retrieval via the index node, mirroring the work-episode store's contract.
/// </summary>
public sealed class GraphEpisodicSegmentStoreTests
{
    private readonly InMemoryGraphStore _graphStore;
    private readonly GraphEpisodicSegmentStore _sut;

    public GraphEpisodicSegmentStoreTests()
    {
        _graphStore = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _sut = new GraphEpisodicSegmentStore(
            _graphStore, StubAmbientRequestScope.None(), Mock.Of<ILogger<GraphEpisodicSegmentStore>>());
    }

    private static EpisodicSegment BuildSegment(
        Guid? id = null,
        Guid? episodeId = null,
        string conversationId = "conv-1",
        int turnNumber = 1,
        string content = "User: hi\nAssistant: hello",
        DateTimeOffset? createdAt = null) => new()
    {
        SegmentId = id ?? Guid.NewGuid(),
        EpisodeId = episodeId ?? Guid.NewGuid(),
        AgentId = "agent-x",
        ConversationId = conversationId,
        TurnNumber = turnNumber,
        Content = content,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SaveThenGet_RoundTripsAllFields()
    {
        var episodeId = Guid.NewGuid();
        var segment = BuildSegment(episodeId: episodeId, content: "User: q\nAssistant: a");

        (await _sut.SaveAsync(segment, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var fetched = (await _sut.GetByConversationAsync("conv-1", CancellationToken.None)).Value;

        fetched.Should().ContainSingle();
        var got = fetched[0];
        got.SegmentId.Should().Be(segment.SegmentId);
        got.EpisodeId.Should().Be(episodeId, "the cross-link back to the work episode must round-trip");
        got.AgentId.Should().Be("agent-x");
        got.ConversationId.Should().Be("conv-1");
        got.TurnNumber.Should().Be(1);
        got.Content.Should().Be("User: q\nAssistant: a");
        got.CreatedAt.Should().Be(segment.CreatedAt);
    }

    [Fact]
    public async Task SaveThenGet_NullEpisodeId_RoundTripsAsNull()
    {
        // Work memory disabled for the turn => no episode cross-link. It must round-trip as null, not empty.
        var segment = new EpisodicSegment
        {
            SegmentId = Guid.NewGuid(),
            EpisodeId = null,
            AgentId = "agent-x",
            ConversationId = "solo",
            TurnNumber = 1,
            Content = "User: hi\nAssistant: hello",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _sut.SaveAsync(segment, CancellationToken.None);
        var fetched = (await _sut.GetByConversationAsync("solo", CancellationToken.None)).Value;

        fetched.Should().ContainSingle();
        fetched[0].EpisodeId.Should().BeNull("a missing episode cross-link stays null, never Guid.Empty");
    }

    [Fact]
    public async Task GetByConversation_ReturnsOnlyThatConversationViaIndex()
    {
        await _sut.SaveAsync(BuildSegment(conversationId: "alpha", turnNumber: 1), CancellationToken.None);
        await _sut.SaveAsync(BuildSegment(conversationId: "alpha", turnNumber: 2), CancellationToken.None);
        await _sut.SaveAsync(BuildSegment(conversationId: "beta", turnNumber: 1), CancellationToken.None);

        var result = await _sut.GetByConversationAsync("alpha", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().OnlyContain(s => s.ConversationId == "alpha");
    }

    [Fact]
    public async Task GetByConversation_Unknown_ReturnsEmpty()
    {
        await _sut.SaveAsync(BuildSegment(conversationId: "alpha"), CancellationToken.None);

        var result = await _sut.GetByConversationAsync("missing", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByConversation_OrdersNewestFirst()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var older = BuildSegment(conversationId: "c", createdAt: baseTime);
        var newer = BuildSegment(conversationId: "c", createdAt: baseTime.AddMinutes(5));
        await _sut.SaveAsync(older, CancellationToken.None);
        await _sut.SaveAsync(newer, CancellationToken.None);

        var result = await _sut.GetByConversationAsync("c", CancellationToken.None);

        result.Value.Should().HaveCount(2);
        result.Value[0].SegmentId.Should().Be(newer.SegmentId);
    }

    [Fact]
    public async Task Save_KeepsRawUntruncatedContent()
    {
        var raw = new string('z', 10_000);
        var segment = BuildSegment(conversationId: "big", content: raw);

        await _sut.SaveAsync(segment, CancellationToken.None);
        var fetched = (await _sut.GetByConversationAsync("big", CancellationToken.None)).Value;

        fetched.Should().ContainSingle();
        fetched[0].Content.Should().Be(raw, "episodic segments are stored raw, never truncated");
    }

    [Fact]
    public async Task Save_UsesDeterministicLowercaseNodeId()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(BuildSegment(id: id), CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"episodicsegment:{id}".ToLowerInvariant(), CancellationToken.None);
        node.Should().NotBeNull();
        node!.Type.Should().Be("EpisodicSegment");
    }

    [Fact]
    public async Task Save_StampsCallerCanonicalOwner_SoOwnerScopedErasureCanFindIt()
    {
        // D3 owner-stamp: an unstamped segment node persists owner-less and escapes owner-scoped
        // right-to-erasure (GetNodesByOwnerAsync). Owner is canonicalized (trimmed/lowercased).
        var sut = new GraphEpisodicSegmentStore(
            _graphStore, StubAmbientRequestScope.ForOwner("  User-42  "), Mock.Of<ILogger<GraphEpisodicSegmentStore>>());
        var segment = BuildSegment();

        await sut.SaveAsync(segment, CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"episodicsegment:{segment.SegmentId}".ToLowerInvariant(), CancellationToken.None);
        node!.OwnerId.Should().Be("user-42", "the saved node must carry the caller's canonical owner");

        var owned = await _graphStore.GetNodesByOwnerAsync("user-42", CancellationToken.None);
        owned.Should().Contain(n => n.Id == node.Id,
            "owner-scoped erasure resolves nodes via GetNodesByOwnerAsync and must reach the episodic-segment node");
    }
}
