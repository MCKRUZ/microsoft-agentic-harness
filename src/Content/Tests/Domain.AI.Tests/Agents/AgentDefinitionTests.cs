using Domain.AI.Agents;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="AgentDefinition"/> record — construction, defaults, equality, and with-expressions.
/// </summary>
public sealed class AgentDefinitionTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var def = new AgentDefinition
        {
            Id = "research-agent",
            Name = "Research Agent"
        };

        def.Id.Should().Be("research-agent");
        def.Name.Should().Be("Research Agent");
    }

    [Fact]
    public void Defaults_OptionalProperties_AreCorrect()
    {
        var def = new AgentDefinition { Id = "test", Name = "Test" };

        def.Description.Should().BeEmpty();
        def.Category.Should().BeNull();
        def.Domain.Should().BeNull();
        def.Version.Should().BeNull();
        def.Author.Should().BeNull();
        def.Tags.Should().BeEmpty();
        def.Skill.Should().BeNull();
        def.FilePath.Should().BeEmpty();
        def.BaseDirectory.Should().BeEmpty();
    }

    [Fact]
    public void LoadedAt_DefaultsToUtcNow()
    {
        var before = DateTime.UtcNow;
        var def = new AgentDefinition { Id = "test", Name = "Test" };
        var after = DateTime.UtcNow;

        def.LoadedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance_PreservesOriginal()
    {
        var original = new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent One",
            Category = "analysis"
        };

        var updated = original with { Category = "research" };

        updated.Category.Should().Be("research");
        original.Category.Should().Be("analysis");
        updated.Id.Should().Be("agent-1");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var loadedAt = DateTime.UtcNow;
        var def1 = new AgentDefinition
        {
            Id = "agent",
            Name = "Agent",
            LoadedAt = loadedAt
        };
        var def2 = new AgentDefinition
        {
            Id = "agent",
            Name = "Agent",
            LoadedAt = loadedAt
        };

        def1.Should().Be(def2);
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var tags = new List<string> { "research", "ml" };
        var loadedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var def = new AgentDefinition
        {
            Id = "ml-agent",
            Name = "ML Research Agent",
            Description = "Conducts ML research",
            Category = "research",
            Domain = "machine-learning",
            Version = "2.0.0",
            Author = "Team A",
            Tags = tags,
            Skill = "ml-research-skill",
            FilePath = "/agents/ml-agent/AGENT.md",
            BaseDirectory = "/agents/ml-agent",
            LoadedAt = loadedAt
        };

        def.Id.Should().Be("ml-agent");
        def.Name.Should().Be("ML Research Agent");
        def.Description.Should().Be("Conducts ML research");
        def.Category.Should().Be("research");
        def.Domain.Should().Be("machine-learning");
        def.Version.Should().Be("2.0.0");
        def.Author.Should().Be("Team A");
        def.Tags.Should().BeEquivalentTo(tags);
        def.Skill.Should().Be("ml-research-skill");
        def.FilePath.Should().Be("/agents/ml-agent/AGENT.md");
        def.BaseDirectory.Should().Be("/agents/ml-agent");
        def.LoadedAt.Should().Be(loadedAt);
    }
}
