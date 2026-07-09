using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Learnings;
using Infrastructure.AI.KnowledgeGraph.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Learnings;

public sealed class GraphLearningsStoreTests
{
    private readonly InMemoryGraphStore _graphStore;
    private readonly GraphLearningsStore _sut;

    public GraphLearningsStoreTests()
    {
        _graphStore = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _sut = new GraphLearningsStore(
            _graphStore,
            StubAmbientRequestScope.None(),
            Mock.Of<ILogger<GraphLearningsStore>>());
    }

    private static LearningEntry BuildEntry(
        Guid? id = null,
        string? agentId = null,
        string? teamId = null,
        bool isGlobal = false,
        LearningCategory category = LearningCategory.DomainKnowledge,
        double feedbackWeight = 1.0,
        string content = "Test learning content") => new()
    {
        LearningId = id ?? Guid.NewGuid(),
        Content = content,
        Category = category,
        DecayClass = DecayClass.Stable,
        FeedbackWeight = feedbackWeight,
        UpdateCount = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        Scope = new LearningScope
        {
            AgentId = agentId,
            TeamId = teamId,
            IsGlobal = isGlobal
        },
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "test-source",
            SourceDescription = "Test source"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test-pipeline",
            OriginTask = "test-task",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.95
        }
    };

    [Fact]
    public async Task Save_StampsCallerCanonicalOwner_SoOwnerScopedErasureCanFindIt()
    {
        // D3 owner-stamp: an unstamped learning node persists owner-less and escapes owner-scoped
        // right-to-erasure (GetNodesByOwnerAsync). Owner is canonicalized (trimmed/lowercased).
        var sut = new GraphLearningsStore(
            _graphStore, StubAmbientRequestScope.ForOwner("  User-42  "), Mock.Of<ILogger<GraphLearningsStore>>());
        var id = Guid.NewGuid();

        await sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
        node!.OwnerId.Should().Be("user-42", "the saved node must carry the caller's canonical owner");

        var owned = await _graphStore.GetNodesByOwnerAsync("user-42", CancellationToken.None);
        owned.Should().Contain(n => n.Id == node.Id,
            "owner-scoped erasure resolves nodes via GetNodesByOwnerAsync and must reach the learning node");
    }

    [Fact]
    public async Task Save_Graph_CreatesNodeWithDeterministicId()
    {
        var id = Guid.NewGuid();
        var entry = BuildEntry(id: id, isGlobal: true);

        await _sut.SaveAsync(entry, CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
        node.Should().NotBeNull();
        node!.Type.Should().Be("LearningEntry");
        node.Properties["Content"].Should().Be(entry.Content);
        node.Properties["Category"].Should().Be("DomainKnowledge");
        node.Properties["DecayClass"].Should().Be("Stable");
    }

    [Fact]
    public async Task Save_Graph_CreatesIndexEdges_AgentScope()
    {
        var entry = BuildEntry(agentId: "agent-1");

        await _sut.SaveAsync(entry, CancellationToken.None);

        var indexExists = await _graphStore.NodeExistsAsync("learningindex:agent:agent-1", CancellationToken.None);
        indexExists.Should().BeTrue();

        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:agent:agent-1", maxDepth: 1, CancellationToken.None);
        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
    }

    [Fact]
    public async Task Save_Graph_CreatesIndexEdges_TeamScope()
    {
        var entry = BuildEntry(teamId: "team-1");

        await _sut.SaveAsync(entry, CancellationToken.None);

        var indexExists = await _graphStore.NodeExistsAsync("learningindex:team:team-1", CancellationToken.None);
        indexExists.Should().BeTrue();

        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:team:team-1", maxDepth: 1, CancellationToken.None);
        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
    }

    [Fact]
    public async Task Save_Graph_CreatesIndexEdges_GlobalScope()
    {
        var entry = BuildEntry(isGlobal: true);

        await _sut.SaveAsync(entry, CancellationToken.None);

        var indexExists = await _graphStore.NodeExistsAsync("learningindex:global", CancellationToken.None);
        indexExists.Should().BeTrue();

        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:global", maxDepth: 1, CancellationToken.None);
        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
    }

    [Fact]
    public async Task Save_Graph_CreatesMultipleIndexEdges()
    {
        var entry = BuildEntry(agentId: "a1", teamId: "t1", isGlobal: true);

        await _sut.SaveAsync(entry, CancellationToken.None);

        (await _graphStore.NodeExistsAsync("learningindex:agent:a1", CancellationToken.None)).Should().BeTrue();
        (await _graphStore.NodeExistsAsync("learningindex:team:t1", CancellationToken.None)).Should().BeTrue();
        (await _graphStore.NodeExistsAsync("learningindex:global", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Get_Graph_RetrievesByDeterministicId()
    {
        var id = Guid.NewGuid();
        var entry = BuildEntry(id: id, isGlobal: true, content: "Specific learning content");

        await _sut.SaveAsync(entry, CancellationToken.None);

        var result = await _sut.GetAsync(id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.LearningId.Should().Be(id);
        result.Value.Content.Should().Be("Specific learning content");
        result.Value.Category.Should().Be(LearningCategory.DomainKnowledge);
        result.Value.Scope.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNull()
    {
        var result = await _sut.GetAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Search_AgentScope_ReturnsAgentLearnings()
    {
        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(agentId: "agent-2"), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "agent-1" }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_TeamScope_ReturnsTeamLearnings()
    {
        await _sut.SaveAsync(BuildEntry(teamId: "team-1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(teamId: "team-2"), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { TeamId = "team-1" }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_GlobalScope_ReturnsGlobalLearnings()
    {
        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { IsGlobal = true }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_ScopeHierarchy_MergesAllLevels()
    {
        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_DeduplicatesByLearningId()
    {
        var entry = BuildEntry(agentId: "a1", teamId: "t1", isGlobal: true);
        await _sut.SaveAsync(entry, CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_ExcludesSoftDeleted()
    {
        var keepId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        await _sut.SaveAsync(BuildEntry(id: keepId, isGlobal: true), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(id: deleteId, isGlobal: true), CancellationToken.None);

        await _sut.SoftDeleteAsync(deleteId, "outdated", CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { IsGlobal = true }
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].LearningId.Should().Be(keepId);
    }

    [Fact]
    public async Task Search_FiltersByCategory()
    {
        await _sut.SaveAsync(BuildEntry(isGlobal: true, category: LearningCategory.FactualCorrection), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true, category: LearningCategory.DomainKnowledge), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { IsGlobal = true },
            Category = LearningCategory.FactualCorrection
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].Category.Should().Be(LearningCategory.FactualCorrection);
    }

    [Fact]
    public async Task SoftDelete_SetsIsDeletedFlag()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);

        await _sut.SoftDeleteAsync(id, "test reason", CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
        node.Should().NotBeNull();
        node!.Properties["IsDeleted"].Should().Be("true");
    }

    [Fact]
    public async Task SoftDelete_SetsDeleteReason()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);

        await _sut.SoftDeleteAsync(id, "outdated", CancellationToken.None);

        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
        node!.Properties["DeleteReason"].Should().Be("outdated");
    }

    [Fact]
    public async Task SoftDelete_NotFound_ReturnsFail()
    {
        var result = await _sut.SoftDeleteAsync(Guid.NewGuid(), "test", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Update_PreservesGraphNodeId()
    {
        var id = Guid.NewGuid();
        var entry = BuildEntry(id: id, isGlobal: true, feedbackWeight: 1.0);
        await _sut.SaveAsync(entry, CancellationToken.None);

        var updated = entry with { FeedbackWeight = 2.5 };
        var result = await _sut.UpdateAsync(updated, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
        node.Should().NotBeNull();
        node!.Properties["FeedbackWeight"].Should().Contain("2.5");
    }

    [Fact]
    public async Task Get_SoftDeleted_ReturnsNull()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);
        await _sut.SoftDeleteAsync(id, "gone", CancellationToken.None);

        var result = await _sut.GetAsync(id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Search_NullScope_ReturnsAllLearnings()
    {
        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);

        var criteria = new LearningSearchCriteria { Scope = null };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsFail()
    {
        var entry = BuildEntry(id: Guid.NewGuid(), isGlobal: true);

        var result = await _sut.UpdateAsync(entry, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Get_RoundTrips_AllSerializedFields()
    {
        var id = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var reinforced = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var entry = new LearningEntry
        {
            LearningId = id,
            Content = "Round-trip test content",
            Category = LearningCategory.FactualCorrection,
            DecayClass = DecayClass.Volatile,
            FeedbackWeight = 0.876543,
            UpdateCount = 3,
            CreatedAt = created,
            LastReinforcedAt = reinforced,
            LastAccessedAt = reinforced,
            Scope = new LearningScope { AgentId = "agent-x", TeamId = "team-y", IsGlobal = true },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "src-123",
                SourceDescription = "Human review"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "pipeline-a",
                OriginTask = "task-b",
                OriginTimestamp = created,
                Confidence = 0.9521
            }
        };

        await _sut.SaveAsync(entry, CancellationToken.None);
        var result = await _sut.GetAsync(id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var got = result.Value!;
        got.LearningId.Should().Be(id);
        got.Content.Should().Be("Round-trip test content");
        got.Category.Should().Be(LearningCategory.FactualCorrection);
        got.DecayClass.Should().Be(DecayClass.Volatile);
        got.FeedbackWeight.Should().BeApproximately(0.876543, 0.0001);
        got.UpdateCount.Should().Be(3);
        got.CreatedAt.Should().Be(created);
        got.LastReinforcedAt.Should().Be(reinforced);
        got.LastAccessedAt.Should().Be(reinforced);
        got.Scope.AgentId.Should().Be("agent-x");
        got.Scope.TeamId.Should().Be("team-y");
        got.Scope.IsGlobal.Should().BeTrue();
        got.Source.SourceType.Should().Be(LearningSourceType.HumanCorrection);
        got.Source.SourceId.Should().Be("src-123");
        got.Source.SourceDescription.Should().Be("Human review");
        got.Provenance.OriginPipeline.Should().Be("pipeline-a");
        got.Provenance.OriginTask.Should().Be("task-b");
        got.Provenance.OriginTimestamp.Should().Be(created);
        got.Provenance.Confidence.Should().BeApproximately(0.9521, 0.001);
    }

    [Fact]
    public async Task Search_FiltersByMinFeedbackWeight()
    {
        await _sut.SaveAsync(BuildEntry(isGlobal: true, feedbackWeight: 0.3), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true, feedbackWeight: 0.8), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true, feedbackWeight: 1.5), CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { IsGlobal = true },
            MinFeedbackWeight = 0.7
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().OnlyContain(e => e.FeedbackWeight >= 0.7);
    }

    [Fact]
    public async Task Search_FiltersByCreatedAfterAndBefore()
    {
        var early = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mid = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero);

        await _sut.SaveAsync(BuildEntry(isGlobal: true) with { CreatedAt = early }, CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true) with { CreatedAt = mid }, CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true) with { CreatedAt = late }, CancellationToken.None);

        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { IsGlobal = true },
            CreatedAfter = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedBefore = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _sut.SearchAsync(criteria, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
    }
}
