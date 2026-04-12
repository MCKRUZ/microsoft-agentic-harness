using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for SkillMetadataParser extraction of ## Objectives and ## Trace Format sections.
/// </summary>
public sealed class SkillParserExtensionTests
{
    private static SkillMetadataParser CreateParser() =>
        new(NullLogger<SkillMetadataParser>.Instance);

    [Fact]
    public void SkillParser_WithObjectivesSection_ExtractsObjectivesContent()
    {
        var parser = CreateParser();
        const string body = """

            ## Instructions

            Do the thing.

            ## Objectives

            - Succeed at the thing.

            """;

        using var dir = new TempDirectory();
        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);

        skill.Objectives.Should().NotBeNullOrWhiteSpace();
        skill.Objectives.Should().Contain("Succeed at the thing");
    }

    [Fact]
    public void SkillParser_WithTraceFormatSection_ExtractsTraceFormatContent()
    {
        var parser = CreateParser();
        const string body = """

            ## Instructions

            Do the thing.

            ## Trace Format

            Traces live under traces/{run_id}/.

            """;

        using var dir = new TempDirectory();
        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);

        skill.TraceFormat.Should().NotBeNullOrWhiteSpace();
        skill.TraceFormat.Should().Contain("traces/{run_id}");
    }

    [Fact]
    public void SkillParser_WithoutObjectivesSection_ReturnsNullObjectives()
    {
        var parser = CreateParser();
        const string body = """

            ## Instructions

            Do the thing.

            """;

        using var dir = new TempDirectory();
        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);

        skill.Objectives.Should().BeNull();
    }

    [Fact]
    public void SkillParser_WithoutTraceFormatSection_ReturnsNullTraceFormat()
    {
        var parser = CreateParser();
        const string body = """

            ## Instructions

            Do the thing.

            """;

        using var dir = new TempDirectory();
        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);

        skill.TraceFormat.Should().BeNull();
    }

    [Fact]
    public void SkillParser_ExtractedSections_AreRemovedFromInstructions()
    {
        var parser = CreateParser();
        const string body = """

            ## Instructions

            Do the thing.

            ## Objectives

            - Succeed at the thing.

            ## Trace Format

            Traces live under traces/{run_id}/.

            """;

        using var dir = new TempDirectory();
        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);

        skill.Instructions.Should().NotContain("## Objectives");
        skill.Instructions.Should().NotContain("## Trace Format");
        skill.Instructions.Should().NotContain("Succeed at the thing");
        skill.Instructions.Should().NotContain("traces/{run_id}");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            System.IO.Path.GetRandomFileName());

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
