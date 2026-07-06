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

    // --- GraphNode harmonic property helpers ---

    [Fact]
    public void WithAbstraction_RoundTripsAbstractionAndCueAnchors()
    {
        var node = BareNode();

        var stamped = node.WithAbstraction(new MemoryAbstraction
        {
            Abstraction = "Project Orion timeline",
            CueAnchors = ["Orion milestones", "Orion schedule"]
        });

        stamped.GetAbstraction().Should().Be("Project Orion timeline");
        stamped.GetCueAnchors().Should().BeEquivalentTo("Orion milestones", "Orion schedule");
    }

    [Fact]
    public void WithAbstraction_EmptyCueAnchors_OmitsCueAnchorKey()
    {
        var stamped = BareNode().WithAbstraction(new MemoryAbstraction { Abstraction = "solo" });

        stamped.GetCueAnchors().Should().BeEmpty();
        stamped.Properties.Should().NotContainKey(GraphNodeMemoryExtensions.CueAnchorsPropertyKey);
    }

    [Fact]
    public void WithAbstraction_PreservesExistingProperties_IncludingTrust()
    {
        var node = BareNode().WithTrust(MemoryTrust.Untrusted);

        var stamped = node.WithAbstraction(new MemoryAbstraction { Abstraction = "kept" });

        stamped.GetTrust().Should().Be(MemoryTrust.Untrusted, "stamping an abstraction must not drop the trust marker");
        stamped.Properties["content"].Should().Be("body");
        stamped.GetAbstraction().Should().Be("kept");
    }

    [Fact]
    public void GetAbstraction_OnNodeWithoutAbstraction_ReturnsNull()
    {
        BareNode().GetAbstraction().Should().BeNull();
    }

    [Fact]
    public void GetCueAnchors_OnNodeWithoutAnchors_ReturnsEmpty()
    {
        BareNode().GetCueAnchors().Should().BeEmpty();
    }

    [Fact]
    public void WithAbstraction_TrimsAndDropsBlankCueAnchors()
    {
        var stamped = BareNode().WithAbstraction(new MemoryAbstraction
        {
            Abstraction = "topic",
            CueAnchors = ["  spaced anchor  ", "   ", "second"]
        });

        stamped.GetCueAnchors().Should().BeEquivalentTo("spaced anchor", "second");
    }

    private static GraphNode BareNode() => new()
    {
        Id = "memory:default:anon:k",
        Name = "k",
        Type = "Fact",
        Properties = new Dictionary<string, string> { ["content"] = "body" }
    };
}
