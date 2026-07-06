using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

public sealed class HarmonicMemoryModelsTests
{
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
