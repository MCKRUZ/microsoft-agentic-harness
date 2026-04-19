using Domain.AI.A2A;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.A2A;

/// <summary>
/// Tests for <see cref="AgentCard"/> record — construction, defaults, equality, and with-expressions.
/// </summary>
public sealed class AgentCardTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var card = new AgentCard
        {
            Name = "research-agent",
            Description = "Finds and analyzes information"
        };

        card.Name.Should().Be("research-agent");
        card.Description.Should().Be("Finds and analyzes information");
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNullOrEmpty()
    {
        var card = new AgentCard { Name = "test", Description = "test" };

        card.Url.Should().BeNull();
        card.Capabilities.Should().BeEmpty();
        card.Skills.Should().BeEmpty();
        card.Version.Should().BeNull();
    }

    [Fact]
    public void WithExpression_CreatesNewInstanceWithUpdatedValue()
    {
        var original = new AgentCard
        {
            Name = "agent-v1",
            Description = "Version 1",
            Version = "1.0.0"
        };

        var updated = original with { Version = "2.0.0" };

        updated.Version.Should().Be("2.0.0");
        updated.Name.Should().Be("agent-v1");
        original.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var capabilities = new List<string> { "search", "analyze" };
        var card1 = new AgentCard
        {
            Name = "agent",
            Description = "desc",
            Capabilities = capabilities
        };
        var card2 = new AgentCard
        {
            Name = "agent",
            Description = "desc",
            Capabilities = capabilities
        };

        card1.Should().Be(card2);
    }

    [Fact]
    public void Equality_DifferentName_AreNotEqual()
    {
        var card1 = new AgentCard { Name = "agent-a", Description = "desc" };
        var card2 = new AgentCard { Name = "agent-b", Description = "desc" };

        card1.Should().NotBe(card2);
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var capabilities = new List<string> { "streaming", "tool-use" };
        var skills = new List<string> { "research", "analysis" };

        var card = new AgentCard
        {
            Name = "full-agent",
            Description = "Full agent card",
            Url = "https://example.com/a2a",
            Capabilities = capabilities,
            Skills = skills,
            Version = "3.1.0"
        };

        card.Name.Should().Be("full-agent");
        card.Description.Should().Be("Full agent card");
        card.Url.Should().Be("https://example.com/a2a");
        card.Capabilities.Should().BeEquivalentTo(capabilities);
        card.Skills.Should().BeEquivalentTo(skills);
        card.Version.Should().Be("3.1.0");
    }
}
