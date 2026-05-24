using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillMetadataParser"/> covering nested YAML <c>metadata:</c> block parsing.
/// </summary>
public sealed class SkillMetadataParserNestedYamlTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserNestedYamlTests()
    {
        _sut = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"skill-nested-yaml-tests-{Guid.NewGuid():N}");
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
    public void ParseFromFile_NestedMetadata_ExtractsMetadataDictionary()
    {
        var skillContent = """
            ---
            name: "azure-skill"
            version: "2.0"
            metadata:
              author: Microsoft
              version: "1.1.2"
              team: Azure
            ---
            # Instructions
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Metadata.Should().NotBeNull();
        result.Metadata.Should().ContainKey("author");
        result.Metadata!["author"].Should().Be("Microsoft");
        result.Metadata.Should().ContainKey("version");
        result.Metadata["version"].Should().Be("1.1.2");
        result.Metadata.Should().ContainKey("team");
        result.Metadata["team"].Should().Be("Azure");
    }

    [Fact]
    public void ParseFromFile_NestedMetadata_DoesNotPolluteTopLevelFields()
    {
        var skillContent = """
            ---
            name: "azure-skill"
            version: "2.0"
            metadata:
              author: Microsoft
              version: "1.1.2"
            ---
            # Instructions
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        // Top-level version must not be overridden by nested metadata.version
        result.Version.Should().Be("2.0");
    }

    [Fact]
    public void ParseFromFile_NoMetadataBlock_MetadataIsNull()
    {
        var skillContent = """
            ---
            name: "simple-skill"
            version: "1.0"
            ---
            # Instructions
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Metadata.Should().BeNull();
        result.Author.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_NestedMetadata_PopulatesAuthorField()
    {
        var skillContent = """
            ---
            name: "authored-skill"
            metadata:
              author: "Jane Doe"
              version: "3.0"
            ---
            # Instructions
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Author.Should().Be("Jane Doe");
    }

    [Fact]
    public void ParseFromFile_TopLevelVersion_NotOverriddenByNestedVersion()
    {
        var skillContent = """
            ---
            name: "versioned-skill"
            version: "5.0"
            metadata:
              author: Contoso
              version: "0.0.1"
            ---
            # Instructions
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Version.Should().Be("5.0");
        result.Metadata.Should().NotBeNull();
        result.Metadata!["version"].Should().Be("0.0.1");
    }

    [Fact]
    public void ParseFromFile_MixedNestedAndTopLevel_ParsesCorrectly()
    {
        var skillContent = """
            ---
            name: "mixed-skill"
            category: "platform"
            version: "4.2"
            tags: ["infra", "core"]
            metadata:
              author: "MCKRUZ"
              team: "Platform"
              license: "MIT"
            ---
            # Instructions

            Do the mixed thing.
            """;

        var filePath = WriteSkillFile(skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        // Top-level fields parse correctly
        result.Name.Should().Be("mixed-skill");
        result.Category.Should().Be("platform");
        result.Version.Should().Be("4.2");
        result.Tags.Should().BeEquivalentTo(["infra", "core"]);

        // Nested metadata is populated
        result.Author.Should().Be("MCKRUZ");
        result.Metadata.Should().NotBeNull();
        result.Metadata!["team"].Should().Be("Platform");
        result.Metadata["license"].Should().Be("MIT");

        // Nested keys did not bleed into top-level fields
        result.Metadata.Should().NotContainKey("name");
        result.Metadata.Should().NotContainKey("category");
    }
}
