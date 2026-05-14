using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Learnings;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Learnings;

public sealed class InMemoryLearningsStoreTests
{
    private readonly InMemoryLearningsStore _sut = new();

    private static LearningEntry BuildEntry(
        Guid? id = null,
        string? agentId = null,
        string? teamId = null,
        bool isGlobal = false,
        LearningCategory category = LearningCategory.DomainKnowledge) => new()
    {
        LearningId = id ?? Guid.NewGuid(),
        Content = "Test learning",
        Category = category,
        DecayClass = DecayClass.Stable,
        FeedbackWeight = 1.0,
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
            SourceId = "test",
            SourceDescription = "Test"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test",
            OriginTask = "test",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 1.0
        }
    };

    [Fact]
    public async Task InMemory_SaveAndRetrieve_RoundTrips()
    {
        var id = Guid.NewGuid();
        var entry = BuildEntry(id: id, isGlobal: true);

        await _sut.SaveAsync(entry, CancellationToken.None);
        var result = await _sut.GetAsync(id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.LearningId.Should().Be(id);
        result.Value.Content.Should().Be("Test learning");
        result.Value.Scope.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task InMemory_ScopeHierarchySearch_Works()
    {
        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);

        var allScopes = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
        };
        var allResult = await _sut.SearchAsync(allScopes, CancellationToken.None);
        allResult.Value.Should().HaveCount(3);

        var agentOnly = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "a1" }
        };
        var agentResult = await _sut.SearchAsync(agentOnly, CancellationToken.None);
        agentResult.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task InMemory_SoftDelete_ExcludesFromSearch()
    {
        var keepId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        await _sut.SaveAsync(BuildEntry(id: keepId, isGlobal: true), CancellationToken.None);
        await _sut.SaveAsync(BuildEntry(id: deleteId, isGlobal: true), CancellationToken.None);

        await _sut.SoftDeleteAsync(deleteId, "test", CancellationToken.None);

        var result = await _sut.SearchAsync(
            new LearningSearchCriteria { Scope = new LearningScope { IsGlobal = true } },
            CancellationToken.None);

        result.Value.Should().ContainSingle();
        result.Value[0].LearningId.Should().Be(keepId);
    }

    [Fact]
    public async Task InMemory_Update_NotFound_ReturnsFail()
    {
        var entry = BuildEntry(id: Guid.NewGuid());

        var result = await _sut.UpdateAsync(entry, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}
