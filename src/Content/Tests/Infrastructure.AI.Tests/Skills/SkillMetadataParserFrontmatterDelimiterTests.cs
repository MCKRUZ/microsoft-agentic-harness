using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Pins that the frontmatter/body split requires the closing <c>---</c> to occupy its own line,
/// not merely appear anywhere in the file.
/// </summary>
/// <remarks>
/// A previous version of <see cref="SkillMetadataParser"/> searched for the closing delimiter with
/// <c>raw.IndexOf("---", 3)</c>, which matches <c>---</c> anywhere — including inside a YAML
/// value. A description written as <c>"compare A --- B"</c> would truncate the frontmatter at that
/// inline dash run, mid-way through the still-open quoted string. Mutation-tested: reverting the
/// parser to the old <c>IndexOf("---", 3)</c> search makes this test fail — not by silently
/// dropping the trailing fields as the truncation was expected to do, but by handing YamlDotNet an
/// unterminated quoted scalar, which it refuses to parse at all
/// (<c>YamlDotNet.Core.SyntaxErrorException: While scanning a quoted scalar, found unexpected end
/// of stream</c>), and <see cref="SkillFrontmatter.Load"/> rejects the whole skill rather than load
/// it with fields missing. Confirmed by reverting the fix and running this test: this is the actual
/// observed failure, not a hypothetical one.
/// </remarks>
public sealed class SkillMetadataParserFrontmatterDelimiterTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserFrontmatterDelimiterTests()
    {
        _sut = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader());
        _tempDir = Path.Combine(Path.GetTempPath(), $"skill-parser-delimiter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ParseFromFile_InlineDashRunInsideAYamlValue_DoesNotTruncateFrontmatter()
    {
        var skillContent = """
            ---
            name: "diff-reviewer"
            description: "Compares A --- B and reports the difference"
            category: "development"
            version: "1.0"
            ---
            # Diff Reviewer Instructions

            Compare the two inputs and report every difference.
            """;

        var filePath = Path.Combine(_tempDir, "SKILL.md");
        File.WriteAllText(filePath, skillContent);

        var result = _sut.ParseFromFile(filePath, _tempDir);

        result.Description.Should().Be("Compares A --- B and reports the difference");
        result.Category.Should().Be("development");
        result.Version.Should().Be("1.0");
        result.Instructions.Should().Contain("Compare the two inputs and report every difference.");
        result.Instructions.Should().NotContain("category:");
        result.Instructions.Should().NotContain("version:");
    }
}
