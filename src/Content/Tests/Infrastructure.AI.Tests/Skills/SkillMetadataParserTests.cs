using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillMetadataParser"/> covering frontmatter extraction,
/// structured section parsing, and edge cases in SKILL.md parsing.
/// </summary>
public sealed class SkillMetadataParserTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserTests()
    {
        _sut = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"skill-parser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSkillFile(string content)
    {
        var filePath = Path.Combine(_tempDir, "SKILL.md");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public void ParseFromFile_FullFrontmatter_ExtractsAllFields()
    {
        var skillContent = """
            ---
            name: "code-reviewer"
            description: "Reviews code for quality"
            category: "development"
            skill_type: "analysis"
            version: "2.0"
            model-override: "gpt-4o"
            agent-id: "reviewer-agent"
            tags: ["code", "review", "quality"]
            allowed-tools: ["file_system", "search"]
            ---
            # Code Review Instructions

            Review all files carefully.
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Name.Should().Be("code-reviewer");
        result.Description.Should().Be("Reviews code for quality");
        result.Category.Should().Be("development");
        result.SkillType.Should().Be("analysis");
        result.Version.Should().Be("2.0");
        result.ModelOverride.Should().Be("gpt-4o");
        result.AgentId.Should().Be("reviewer-agent");
        result.Tags.Should().BeEquivalentTo(["code", "review", "quality"]);
        result.AllowedTools.Should().BeEquivalentTo(["file_system", "search"]);
        result.IsFullyLoaded.Should().BeTrue();
    }

    [Fact]
    public void ParseFromFile_NoFrontmatter_UsesDirectoryNameAsId()
    {
        var skillContent = "# Simple Skill\n\nJust instructions.";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        var expectedName = Path.GetFileName(_tempDir);
        result.Name.Should().Be(expectedName);
        result.Id.Should().Be(expectedName);
        result.Description.Should().BeEmpty();
        result.Instructions.Should().Contain("Simple Skill");
    }

    [Fact]
    public void ParseFromFile_WithObjectives_ExtractsObjectivesSection()
    {
        var skillContent = """
            ---
            name: "test-skill"
            ---
            # Main instructions

            Do the thing.

            ## Objectives

            - Objective 1
            - Objective 2

            ## Other Section

            More content.
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Objectives.Should().Contain("Objective 1");
        result.Objectives.Should().Contain("Objective 2");
        result.Instructions.Should().NotContain("Objective 1");
        result.Instructions.Should().Contain("Other Section");
    }

    [Fact]
    public void ParseFromFile_WithTraceFormat_ExtractsTraceFormatSection()
    {
        var skillContent = """
            ---
            name: "traced-skill"
            ---
            # Instructions

            ## Trace Format

            ```json
            { "action": "string", "result": "string" }
            ```

            ## Next Section

            More stuff.
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.TraceFormat.Should().Contain("action");
        result.Instructions.Should().NotContain("Trace Format");
        result.Instructions.Should().Contain("Next Section");
    }

    [Fact]
    public void ParseFromFile_SetsFilePathAndBaseDirectory()
    {
        var skillContent = "---\nname: test\n---\nBody.";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.FilePath.Should().Be(filePath);
        result.BaseDirectory.Should().Be(_tempDir);
    }

    [Fact]
    public void ParseFromFile_SetsLoadedAtToUtcNow()
    {
        var skillContent = "---\nname: test\n---\nBody.";
        var filePath = WriteSkillFile(skillContent);
        var before = DateTime.UtcNow;

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.LoadedAt.Should().BeOnOrAfter(before);
        result.LoadedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ParseFromFile_EmptyTags_ReturnsEmptyList()
    {
        var skillContent = "---\nname: no-tags\n---\nBody.";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Tags.Should().BeEmpty();
        result.AllowedTools.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_PartialFrontmatter_MissingClosingDelimiter_TreatsAsBody()
    {
        var skillContent = "---\nname: broken\nNo closing delimiter here\nSome body content.";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Name.Should().NotBeNull();
    }

    [Fact]
    public void Parse_WithPreParsedFields_SetsNameAndDescription()
    {
        var result = _sut.Parse(
            "pre-parsed-skill",
            "A pre-parsed description",
            "# Instructions\n\nDo something.",
            _tempDir);

        result.Name.Should().Be("pre-parsed-skill");
        result.Id.Should().Be("pre-parsed-skill");
        result.Description.Should().Be("A pre-parsed description");
        result.Instructions.Should().Contain("Do something.");
    }

    [Fact]
    public void Parse_NullDescription_DefaultsToEmpty()
    {
        var result = _sut.Parse("skill", null, "body", _tempDir);

        result.Description.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WithSkillFileOnDisk_ExtractsCustomFrontmatter()
    {
        var skillContent = """
            ---
            name: "disk-skill"
            category: "testing"
            version: "1.0"
            ---
            Body from disk.
            """;
        WriteSkillFile(skillContent);

        var result = _sut.Parse("disk-skill", "desc", "Instructions body", _tempDir);

        result.Category.Should().Be("testing");
        result.Version.Should().Be("1.0");
    }

    [Fact]
    public void Parse_NoSkillFileOnDisk_ReturnsNullCustomFields()
    {
        var nonExistentDir = Path.Combine(_tempDir, "nonexistent");
        Directory.CreateDirectory(nonExistentDir);

        var result = _sut.Parse("orphan", "desc", "body", nonExistentDir);

        result.Category.Should().BeNull();
        result.Version.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_ObjectivesInsideCodeFence_NotExtracted()
    {
        var skillContent = """
            ---
            name: "fenced"
            ---
            # Instructions

            ```markdown
            ## Objectives

            - This should NOT be extracted
            ```

            ## Objectives

            - This SHOULD be extracted
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Objectives.Should().Contain("This SHOULD be extracted");
        result.Objectives.Should().NotContain("This should NOT be extracted");
    }

    [Fact]
    public void ParseFromFile_QuotedAndUnquotedValues_BothParsed()
    {
        var skillContent = "---\nname: unquoted-name\ndescription: 'single quoted'\ncategory: \"double quoted\"\n---\nBody.";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Name.Should().Be("unquoted-name");
        result.Description.Should().Be("single quoted");
        result.Category.Should().Be("double quoted");
    }

    [Fact]
    public void ParseFromFile_EmptyBody_ReturnsEmptyInstructions()
    {
        var skillContent = "---\nname: empty-body\n---\n";
        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Instructions.Should().BeEmpty();
    }
}
