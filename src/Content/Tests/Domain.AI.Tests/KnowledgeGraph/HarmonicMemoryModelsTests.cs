using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

public sealed class HarmonicMemoryModelsTests
{
    private static MemoryRecord NewRecord() => new()
    {
        Id = "id-1",
        Content = "content",
        Source = "session-1",
        Weight = 0.5,
        CreatedAt = DateTimeOffset.UnixEpoch,
        LastAccessedAt = DateTimeOffset.UnixEpoch,
        AccessCount = 0,
    };

    [Fact]
    public void MemoryRecord_LegacyDefaults_HaveNoAbstractionAndEmptyCueAnchors()
    {
        var record = NewRecord();

        record.Abstraction.Should().BeNull();
        record.CueAnchors.Should().BeEmpty();
    }

    [Fact]
    public void MemoryRecord_WithHarmonicFields_RoundTrips()
    {
        var record = NewRecord() with
        {
            Abstraction = "Project Orion Timeline",
            CueAnchors = ["Orion milestones", "Orion decisions"],
        };

        record.Abstraction.Should().Be("Project Orion Timeline");
        record.CueAnchors.Should().Equal("Orion milestones", "Orion decisions");
    }

    [Fact]
    public void MemoryAbstraction_DefaultsToEmptyCueAnchors()
    {
        var abstraction = new MemoryAbstraction { Abstraction = "what it is about" };

        abstraction.CueAnchors.Should().BeEmpty();
    }

    [Fact]
    public void ConsolidationDecision_Create_HasNoTargetId()
    {
        var decision = MemoryConsolidationDecision.Create();

        decision.Action.Should().Be(ConsolidationAction.Create);
        decision.TargetId.Should().BeNull();
    }

    [Fact]
    public void ConsolidationDecision_MergeInto_CarriesTargetId()
    {
        var decision = MemoryConsolidationDecision.MergeInto("existing-42");

        decision.Action.Should().Be(ConsolidationAction.Merge);
        decision.TargetId.Should().Be("existing-42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConsolidationDecision_MergeInto_RejectsEmptyTargetId(string? targetId)
    {
        var act = () => MemoryConsolidationDecision.MergeInto(targetId!);

        act.Should().Throw<ArgumentException>().WithParameterName("targetId");
    }
}
