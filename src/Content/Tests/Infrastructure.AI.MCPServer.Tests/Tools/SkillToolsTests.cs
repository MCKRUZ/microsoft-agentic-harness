using System.Text.Json;
using Application.AI.Common.Interfaces;
using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.MCPServer.Tools;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Tools;

public sealed class SkillToolsTests
{
    private static SkillDefinition MakeSkill(
        string id,
        string? category = null,
        string[]? tags = null,
        string? instructions = null) => new()
    {
        Id = id,
        Name = id,
        Description = $"Description for {id}",
        Category = category,
        Tags = tags ?? [],
        Instructions = instructions ?? $"Instructions for {id}",
        Version = "1.0.0"
    };

    private static SkillTools CreateTools(ISkillMetadataRegistry registry) => new(registry);

    // ── list_skills ──────────────────────────────────────────────────────────

    [Fact]
    public void ListSkills_NoCategory_ReturnsAllSkills()
    {
        var skills = new[] { MakeSkill("skill-a"), MakeSkill("skill-b") };
        var registry = Mock.Of<ISkillMetadataRegistry>(r => r.GetAll() == (IReadOnlyList<SkillDefinition>)skills);
        var sut = CreateTools(registry);

        var json = sut.ListSkills();

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("id").GetString().Should().Be("skill-a");
        doc.RootElement[1].GetProperty("id").GetString().Should().Be("skill-b");
    }

    [Fact]
    public void ListSkills_WithCategory_FiltersResults()
    {
        var filtered = new[] { MakeSkill("research-agent", category: "research") };
        var registry = Mock.Of<ISkillMetadataRegistry>(r =>
            r.GetByCategory("research") == (IReadOnlyList<SkillDefinition>)filtered);
        var sut = CreateTools(registry);

        var json = sut.ListSkills(category: "research");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("category").GetString().Should().Be("research");
    }

    [Fact]
    public void ListSkills_EmptyCategory_TreatedAsNoFilter()
    {
        var all = new[] { MakeSkill("skill-a") };
        var registry = Mock.Of<ISkillMetadataRegistry>(r => r.GetAll() == (IReadOnlyList<SkillDefinition>)all);
        var sut = CreateTools(registry);

        var json = sut.ListSkills(category: "  "); // whitespace-only

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void ListSkills_IncludesExpectedFields()
    {
        var skill = MakeSkill("research-agent", category: "research", tags: ["orchestrator"]);
        var registry = Mock.Of<ISkillMetadataRegistry>(r =>
            r.GetAll() == (IReadOnlyList<SkillDefinition>)new[] { skill });
        var sut = CreateTools(registry);

        var json = sut.ListSkills();

        var elem = JsonDocument.Parse(json).RootElement[0];
        elem.GetProperty("id").GetString().Should().Be("research-agent");
        elem.GetProperty("name").GetString().Should().Be("research-agent");
        elem.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        elem.GetProperty("category").GetString().Should().Be("research");
        elem.GetProperty("version").GetString().Should().Be("1.0.0");
        elem.TryGetProperty("tags", out _).Should().BeTrue();
    }

    [Fact]
    public void ListSkills_EmptyRegistry_ReturnsEmptyArray()
    {
        var registry = Mock.Of<ISkillMetadataRegistry>(r =>
            r.GetAll() == (IReadOnlyList<SkillDefinition>)Array.Empty<SkillDefinition>());
        var sut = CreateTools(registry);

        var json = sut.ListSkills();

        json.Should().Be("[]");
    }

    // ── get_skill ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetSkill_KnownId_ReturnsFullDetails()
    {
        var skill = MakeSkill("research-agent", category: "research", instructions: "Do research.");
        var registry = Mock.Of<ISkillMetadataRegistry>(r => r.TryGet("research-agent") == skill);
        var sut = CreateTools(registry);

        var json = sut.GetSkill("research-agent");

        var elem = JsonDocument.Parse(json).RootElement;
        elem.GetProperty("id").GetString().Should().Be("research-agent");
        elem.GetProperty("instructions").GetString().Should().Be("Do research.");
        elem.TryGetProperty("allowedTools", out _).Should().BeTrue();
    }

    [Fact]
    public void GetSkill_UnknownId_ReturnsErrorWithAvailableSkills()
    {
        var available = new[] { MakeSkill("skill-a"), MakeSkill("skill-b") };
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.TryGet("does-not-exist")).Returns((SkillDefinition?)null);
        registry.Setup(r => r.GetAll()).Returns(available);
        var sut = CreateTools(registry.Object);

        var json = sut.GetSkill("does-not-exist");

        var elem = JsonDocument.Parse(json).RootElement;
        elem.GetProperty("error").GetString().Should().Contain("does-not-exist");
        elem.GetProperty("availableSkills").GetString().Should().Contain("skill-a");
    }

    // ── find_skills_by_tag ────────────────────────────────────────────────────

    [Fact]
    public void FindSkillsByTag_SingleTag_ReturnsMatchingSkills()
    {
        var matched = new[] { MakeSkill("orchestrator-agent", tags: ["orchestrator"]) };
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.Is<IEnumerable<string>>(t => t.Contains("orchestrator"))))
            .Returns(matched);
        var sut = CreateTools(registry.Object);

        var json = sut.FindSkillsByTag("orchestrator");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("id").GetString().Should().Be("orchestrator-agent");
    }

    [Fact]
    public void FindSkillsByTag_MultipleTags_PassesAllToRegistry()
    {
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.IsAny<IEnumerable<string>>()))
            .Returns(Array.Empty<SkillDefinition>());
        var sut = CreateTools(registry.Object);

        sut.FindSkillsByTag("orchestrator, research, multi-agent");

        registry.Verify(r => r.GetByTags(
            It.Is<IEnumerable<string>>(tags =>
                tags.Contains("orchestrator") &&
                tags.Contains("research") &&
                tags.Contains("multi-agent"))),
            Times.Once);
    }

    [Fact]
    public void FindSkillsByTag_NoMatches_ReturnsEmptyArray()
    {
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetByTags(It.IsAny<IEnumerable<string>>()))
            .Returns(Array.Empty<SkillDefinition>());
        var sut = CreateTools(registry.Object);

        var json = sut.FindSkillsByTag("nonexistent-tag");

        json.Should().Be("[]");
    }
}
