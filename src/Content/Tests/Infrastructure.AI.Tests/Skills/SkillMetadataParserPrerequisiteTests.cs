using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillMetadataParser"/> covering prerequisites and completion_tool
/// frontmatter extraction from SKILL.md files.
/// </summary>
public sealed class SkillMetadataParserPrerequisiteTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserPrerequisiteTests()
    {
        _sut = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"prereq-parser-tests-{Guid.NewGuid():N}");
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
    public void ParseFromFile_ExtractsPrerequisites()
    {
        var path = WriteSkillFile("---\nname: deploy\nprerequisites: [validate, test]\n---\nDeploy instructions");

        var result = _sut.ParseFromFile(path, _tempDir);

        result.Prerequisites.Should().BeEquivalentTo(["validate", "test"]);
    }

    [Fact]
    public void ParseFromFile_ExtractsCompletionTool()
    {
        var path = WriteSkillFile("---\nname: validate\ncompletion_tool: run_validation\n---\nValidation instructions");

        var result = _sut.ParseFromFile(path, _tempDir);

        result.CompletionTool.Should().Be("run_validation");
    }

    [Fact]
    public void ParseFromFile_NoPrerequisites_ReturnsEmptyList()
    {
        var path = WriteSkillFile("---\nname: simple\n---\nSimple instructions");

        var result = _sut.ParseFromFile(path, _tempDir);

        result.Prerequisites.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_NoCompletionTool_ReturnsNull()
    {
        var path = WriteSkillFile("---\nname: simple\n---\nSimple instructions");

        var result = _sut.ParseFromFile(path, _tempDir);

        result.CompletionTool.Should().BeNull();
    }

    [Fact]
    public void Parse_ExtractsPrerequisitesAndCompletionToolFromDisk()
    {
        WriteSkillFile("---\nname: deploy\nprerequisites: [validate]\ncompletion_tool: deploy_exec\n---\nDeploy");

        var result = _sut.Parse("deploy", "Deploy skill", "Deploy body", _tempDir);

        result.Prerequisites.Should().BeEquivalentTo(["validate"]);
        result.CompletionTool.Should().Be("deploy_exec");
    }

    [Fact]
    public void ParseFromFile_BothPrerequisitesAndCompletionTool_SetsComputedProperties()
    {
        var path = WriteSkillFile("---\nname: deploy\nprerequisites: [validate, test]\ncompletion_tool: deploy_exec\n---\nDeploy");

        var result = _sut.ParseFromFile(path, _tempDir);

        result.Prerequisites.Should().BeEquivalentTo(["validate", "test"]);
        result.CompletionTool.Should().Be("deploy_exec");
        result.HasPrerequisites.Should().BeTrue();
        result.HasCompletionTool.Should().BeTrue();
    }
}
