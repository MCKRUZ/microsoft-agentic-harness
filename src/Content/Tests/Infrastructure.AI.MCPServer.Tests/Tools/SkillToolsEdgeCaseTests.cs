using System.Text.Json;
using Application.AI.Common.Interfaces;
using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.MCPServer.Tools;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Tools;

/// <summary>
/// Edge case tests for <see cref="SkillTools"/> covering empty inputs,
/// special characters, and JSON serialization behavior.
/// </summary>
public sealed class SkillToolsEdgeCaseTests
{
    private static SkillDefinition MakeSkill(
        string id,
        string? category = null,
        string[]? tags = null,
        string? instructions = null,
        string[]? allowedTools = null) => new()
    {
        Id = id,
        Name = id,
        Description = $"Description for {id}",
        Category = category,
        Tags = tags ?? [],
        Instructions = instructions ?? $"Instructions for {id}",
        Version = "1.0.0",
        AllowedTools = allowedTools ?? []
    };

    // -- GetSkill --

    [Fact]
    public void GetSkill_KnownId_IncludesAllowedToolsInResponse()
    {
        var skill = MakeSkill("test-skill", allowedTools: ["Read", "Write", "Bash"]);
        var registry = Mock.Of<ISkillMetadataRegistry>(r => r.TryGet("test-skill") == skill);
        var sut = new SkillTools(registry);

        var json = sut.GetSkill("test-skill");

        var elem = JsonDocument.Parse(json).RootElement;
        elem.GetProperty("allowedTools").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void GetSkill_KnownId_IncludesVersionField()
    {
        var skill = MakeSkill("versioned-skill");
        var registry = Mock.Of<ISkillMetadataRegistry>(r => r.TryGet("versioned-skill") == skill);
        var sut = new SkillTools(registry);

        var json = sut.GetSkill("versioned-skill");

        var elem = JsonDocument.Parse(json).RootElement;
        elem.GetProperty("version").GetString().Should().Be("1.0.0");
    }

    [Fact]
    public void GetSkill_UnknownId_ErrorMessageContainsAllAvailableIds()
    {
        var available = new[]
        {
            MakeSkill("alpha"),
            MakeSkill("beta"),
            MakeSkill("gamma")
        };
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.TryGet("missing")).Returns((SkillDefinition?)null);
        registry.Setup(r => r.GetAll()).Returns(available);
        var sut = new SkillTools(registry.Object);

        var json = sut.GetSkill("missing");

        var elem = JsonDocument.Parse(json).RootElement;
        var availableSkills = elem.GetProperty("availableSkills").GetString();
        availableSkills.Should().Contain("alpha");
        availableSkills.Should().Contain("beta");
        availableSkills.Should().Contain("gamma");
    }

    // -- FindSkillsByTag --

    [Fact]
    public void FindSkillsByTag_EmptyString_PassesEmptyCollectionToRegistry()
    {
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.IsAny<IEnumerable<string>>()))
            .Returns(Array.Empty<SkillDefinition>());
        var sut = new SkillTools(registry.Object);

        var json = sut.FindSkillsByTag("");

        json.Should().Be("[]");
    }

    [Fact]
    public void FindSkillsByTag_WhitespaceOnlyTags_TrimsCorrectly()
    {
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.IsAny<IEnumerable<string>>()))
            .Returns(Array.Empty<SkillDefinition>());
        var sut = new SkillTools(registry.Object);

        sut.FindSkillsByTag("  tag1  ,  tag2  ");

        registry.Verify(r => r.GetByTags(
            It.Is<IEnumerable<string>>(tags =>
                tags.Contains("tag1") && tags.Contains("tag2"))),
            Times.Once);
    }

    [Fact]
    public void FindSkillsByTag_ResultIncludesAllExpectedFields()
    {
        var skill = MakeSkill("found-skill", category: "research", tags: ["test-tag"]);
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.IsAny<IEnumerable<string>>()))
            .Returns(new[] { skill });
        var sut = new SkillTools(registry.Object);

        var json = sut.FindSkillsByTag("test-tag");

        var elem = JsonDocument.Parse(json).RootElement[0];
        elem.GetProperty("id").GetString().Should().Be("found-skill");
        elem.GetProperty("name").GetString().Should().Be("found-skill");
        elem.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        elem.GetProperty("category").GetString().Should().Be("research");
        elem.TryGetProperty("tags", out _).Should().BeTrue();
    }

    // -- ListSkills JSON format --

    [Fact]
    public void ListSkills_OutputIsValidJson()
    {
        var skills = new[]
        {
            MakeSkill("a", category: "cat1"),
            MakeSkill("b", category: "cat2")
        };
        var registry = Mock.Of<ISkillMetadataRegistry>(r =>
            r.GetAll() == (IReadOnlyList<SkillDefinition>)skills);
        var sut = new SkillTools(registry);

        var json = sut.ListSkills();

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void ListSkills_UsesCamelCasePropertyNames()
    {
        var skills = new[] { MakeSkill("camel-test") };
        var registry = Mock.Of<ISkillMetadataRegistry>(r =>
            r.GetAll() == (IReadOnlyList<SkillDefinition>)skills);
        var sut = new SkillTools(registry);

        var json = sut.ListSkills();

        // Properties should be camelCase
        json.Should().Contain("\"id\":");
        json.Should().Contain("\"name\":");
        json.Should().Contain("\"description\":");
        json.Should().NotContain("\"Id\":");
        json.Should().NotContain("\"Name\":");
    }
}
