using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Skills;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Skills;

public sealed class GraphSkillEffectivenessTrackerTests
{
    private readonly GraphSkillEffectivenessTracker _tracker;

    public GraphSkillEffectivenessTrackerTests()
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _tracker = new GraphSkillEffectivenessTracker(
            store, Mock.Of<ILogger<GraphSkillEffectivenessTracker>>());
    }

    [Fact]
    public async Task RecordOutcome_FirstRecord_CreatesEntry()
    {
        await _tracker.RecordOutcomeAsync("research", "factual", true, 0.9);

        var results = await _tracker.GetEffectivenessAsync("factual");
        results.Should().HaveCount(1);
        results[0].SkillId.Should().Be("research");
        results[0].SuccessCount.Should().Be(1);
        results[0].TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordOutcome_MultipleRecords_UpsertsCorrectly()
    {
        await _tracker.RecordOutcomeAsync("research", "factual", true, 0.9);
        await _tracker.RecordOutcomeAsync("research", "factual", false, 0.3);
        await _tracker.RecordOutcomeAsync("research", "factual", true, 0.8);

        var results = await _tracker.GetEffectivenessAsync("factual");
        results.Should().HaveCount(1);
        results[0].SuccessCount.Should().Be(2);
        results[0].TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task GetEffectiveness_RankedBySuccessRate()
    {
        await _tracker.RecordOutcomeAsync("skill-a", "analysis", true);
        await _tracker.RecordOutcomeAsync("skill-a", "analysis", true);
        await _tracker.RecordOutcomeAsync("skill-b", "analysis", true);
        await _tracker.RecordOutcomeAsync("skill-b", "analysis", false);
        await _tracker.RecordOutcomeAsync("skill-b", "analysis", false);

        var results = await _tracker.GetEffectivenessAsync("analysis");
        results.Should().HaveCount(2);
        results[0].SkillId.Should().Be("skill-a"); // 100% success
        results[1].SkillId.Should().Be("skill-b"); // 33% success
    }

    [Fact]
    public async Task GetEffectiveness_UnknownClassification_ReturnsEmpty()
    {
        var results = await _tracker.GetEffectivenessAsync("unknown");
        results.Should().BeEmpty();
    }
}
