using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Skills;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Skills;

public sealed class GraphSkillAmendmentProviderTests
{
    private readonly GraphSkillAmendmentProvider _provider;
    private readonly InMemoryGraphStore _store;

    public GraphSkillAmendmentProviderTests()
    {
        _store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _provider = new GraphSkillAmendmentProvider(
            _store, Mock.Of<ILogger<GraphSkillAmendmentProvider>>());
    }

    [Fact]
    public async Task AddAmendment_CanBeRetrieved()
    {
        var amendment = new SkillAmendment
        {
            Id = "amend-1", SkillId = "research",
            Content = "For customer X, always check billing first",
            LearnedFrom = "user-feedback",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _provider.AddAmendmentAsync(amendment);
        var results = await _provider.GetAmendmentsAsync("research");

        results.Should().HaveCount(1);
        results[0].Content.Should().Be("For customer X, always check billing first");
    }

    [Fact]
    public async Task GetAmendments_MultipleAmendments_OrderedByCreatedAt()
    {
        var earlier = new SkillAmendment
        {
            Id = "amend-1", SkillId = "research",
            Content = "First amendment", LearnedFrom = "feedback",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var later = new SkillAmendment
        {
            Id = "amend-2", SkillId = "research",
            Content = "Second amendment", LearnedFrom = "feedback",
            CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        };

        await _provider.AddAmendmentAsync(earlier);
        await _provider.AddAmendmentAsync(later);
        var results = await _provider.GetAmendmentsAsync("research");

        results.Should().HaveCount(2);
        results[0].Id.Should().Be("amend-1");
        results[1].Id.Should().Be("amend-2");
    }

    [Fact]
    public async Task RemoveAmendment_RemovesFromStore()
    {
        var amendment = new SkillAmendment
        {
            Id = "amend-1", SkillId = "research",
            Content = "Test", LearnedFrom = "feedback",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _provider.AddAmendmentAsync(amendment);

        await _provider.RemoveAmendmentAsync("amend-1");

        var results = await _provider.GetAmendmentsAsync("research");
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAmendments_DifferentSkills_ReturnsCorrectOnes()
    {
        await _provider.AddAmendmentAsync(new SkillAmendment
        {
            Id = "a1", SkillId = "research", Content = "Research note",
            LearnedFrom = "fb", CreatedAt = DateTimeOffset.UtcNow
        });
        await _provider.AddAmendmentAsync(new SkillAmendment
        {
            Id = "a2", SkillId = "analysis", Content = "Analysis note",
            LearnedFrom = "fb", CreatedAt = DateTimeOffset.UtcNow
        });

        var researchAmendments = await _provider.GetAmendmentsAsync("research");
        var analysisAmendments = await _provider.GetAmendmentsAsync("analysis");

        researchAmendments.Should().HaveCount(1);
        analysisAmendments.Should().HaveCount(1);
        researchAmendments[0].Content.Should().Be("Research note");
        analysisAmendments[0].Content.Should().Be("Analysis note");
    }
}
