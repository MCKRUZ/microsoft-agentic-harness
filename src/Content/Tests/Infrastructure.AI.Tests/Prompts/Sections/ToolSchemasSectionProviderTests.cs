using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

/// <summary>
/// Tests for <see cref="ToolSchemasSectionProvider"/> covering tool listing,
/// empty tool set handling, and section metadata.
/// </summary>
public sealed class ToolSchemasSectionProviderTests
{
    [Fact]
    public void SectionType_IsToolSchemas()
    {
        var provider = new ToolSchemasSectionProvider(Enumerable.Empty<ITool>());

        provider.SectionType.Should().Be(SystemPromptSectionType.ToolSchemas);
    }

    [Fact]
    public async Task GetSectionAsync_NoTools_ReturnsNull()
    {
        var provider = new ToolSchemasSectionProvider(Enumerable.Empty<ITool>());

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().BeNull();
    }

    [Fact]
    public async Task GetSectionAsync_WithTools_ListsToolNamesAndDescriptions()
    {
        var tools = new[]
        {
            CreateTool("file_system", "Reads and writes files."),
            CreateTool("search", "Searches code repositories.")
        };

        var provider = new ToolSchemasSectionProvider(tools);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("**file_system**: Reads and writes files.");
        section.Content.Should().Contain("**search**: Searches code repositories.");
        section.Content.Should().Contain("# Available Tools");
    }

    [Fact]
    public async Task GetSectionAsync_IsCacheable()
    {
        var tools = new[] { CreateTool("tool1", "desc") };
        var provider = new ToolSchemasSectionProvider(tools);

        var section = await provider.GetSectionAsync("agent-1");

        section!.IsCacheable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSectionAsync_Priority_Is30()
    {
        var tools = new[] { CreateTool("tool1", "desc") };
        var provider = new ToolSchemasSectionProvider(tools);

        var section = await provider.GetSectionAsync("agent-1");

        section!.Priority.Should().Be(30);
    }

    [Fact]
    public async Task GetSectionAsync_EstimatedTokens_IsPositive()
    {
        var tools = new[] { CreateTool("tool1", "A tool that does things.") };
        var provider = new ToolSchemasSectionProvider(tools);

        var section = await provider.GetSectionAsync("agent-1");

        section!.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetSectionAsync_SectionName_IsToolSchemas()
    {
        var tools = new[] { CreateTool("tool1", "desc") };
        var provider = new ToolSchemasSectionProvider(tools);

        var section = await provider.GetSectionAsync("agent-1");

        section!.Name.Should().Be("Tool Schemas");
    }

    [Fact]
    public void Constructor_NullTools_Throws()
    {
        var act = () => new ToolSchemasSectionProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static ITool CreateTool(string name, string description)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns(description);
        return mock.Object;
    }
}
