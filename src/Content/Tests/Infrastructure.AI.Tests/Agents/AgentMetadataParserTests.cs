using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentMetadataParser"/>. Verifies frontmatter extraction and
/// graceful handling of missing or malformed fields.
/// </summary>
public sealed class AgentMetadataParserTests : IDisposable
{
    private readonly string _tempDir;

    public AgentMetadataParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agentparser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteAgent(string folderName, string content)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AGENT.md");
        File.WriteAllText(path, content);
        return dir;
    }

    private static AgentMetadataParser CreateParser() =>
        new(NullLogger<AgentMetadataParser>.Instance);

    [Fact]
    public void ParseFromFile_WithAllFrontmatterFields_PopulatesDefinition()
    {
        var dir = WriteAgent("sample", """
            ---
            id: sample-agent
            name: Sample Agent
            description: A sample description.
            domain: research
            category: analysis
            version: 1.2.3
            author: Acme Inc
            tags: ["alpha", "beta"]
            ---

            # Sample body
            """);

        var parser = CreateParser();
        var definition = parser.ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("sample-agent");
        definition.Name.Should().Be("Sample Agent");
        definition.Description.Should().Be("A sample description.");
        definition.Domain.Should().Be("research");
        definition.Category.Should().Be("analysis");
        definition.Version.Should().Be("1.2.3");
        definition.Author.Should().Be("Acme Inc");
        definition.Tags.Should().BeEquivalentTo(["alpha", "beta"]);
        definition.FilePath.Should().EndWith("AGENT.md");
        definition.BaseDirectory.Should().Be(dir);
    }

    [Fact]
    public void ParseFromFile_WithoutIdField_DerivesIdFromName()
    {
        var dir = WriteAgent("no-id", """
            ---
            name: Named Agent
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("Named Agent");
        definition.Name.Should().Be("Named Agent");
    }

    [Fact]
    public void ParseFromFile_WithoutNameOrId_FallsBackToFolderName()
    {
        var dir = WriteAgent("folder-fallback", """
            ---
            description: No name or id.
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("folder-fallback");
        definition.Name.Should().Be("folder-fallback");
    }

    [Fact]
    public void ParseFromFile_WithoutFrontmatter_UsesFolderNameAndEmptyDescription()
    {
        var dir = WriteAgent("raw", "# Raw markdown\nNo frontmatter here.");

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("raw");
        definition.Description.Should().Be(string.Empty);
        definition.Tags.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_WithoutTags_ReturnsEmptyTagList()
    {
        var dir = WriteAgent("tagless", """
            ---
            id: tagless
            name: Tagless
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Tags.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_SkillsListInFrontmatter_ParsesAllSkillIds()
    {
        var dir = WriteAgent("multi-skill", """
            ---
            name: content-agent
            skills: [research-topic, make-ppt]
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["research-topic", "make-ppt"]);
    }

    [Fact]
    public void ParseFromFile_SingleSkillInFrontmatter_ParsesAsSingleElementList()
    {
        var dir = WriteAgent("single-skill", """
            ---
            name: single-agent
            skills: [my-skill]
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["my-skill"]);
    }

    [Fact]
    public void ParseFromFile_NoSkillsFrontmatter_ReturnsEmptySkillsList()
    {
        var dir = WriteAgent("no-skills", """
            ---
            name: bare-agent
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_LegacySkillSingular_ParsesAsSingleElementList()
    {
        var dir = WriteAgent("legacy-skill", """
            ---
            name: legacy-agent
            skill: my-skill
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["my-skill"]);
    }
}
