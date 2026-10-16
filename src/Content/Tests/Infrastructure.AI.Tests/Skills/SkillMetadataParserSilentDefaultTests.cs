using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Pins #840: a file-loaded skill whose frontmatter block has no usable <c>description</c> leaves a
/// trace. It still loads, because a SKILL.md with no frontmatter at all is a pinned legitimate shape;
/// what was missing was any log. The warning IS the behaviour under test: nothing else about the
/// loaded skill changes. A frontmatter <c>name</c> that differs from the directory is deliberately
/// NOT warned about (see the last test): the frontmatter name is the skill's id, and the eval harness
/// builds the directory from the candidate, so the difference is not an authoring mistake there.
/// </summary>
public sealed class SkillMetadataParserSilentDefaultTests : IDisposable
{
    private readonly CapturingLogger<SkillMetadataParser> _logger = new();
    private readonly SkillMetadataParser _sut;
    private readonly string _root;

    public SkillMetadataParserSilentDefaultTests()
    {
        _sut = new SkillMetadataParser(
            _logger, new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig(),
            TestMcpSecurityScanner.RealEgressValidator());
        _root = Path.Combine(Path.GetTempPath(), $"skill-silent-default-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (string FilePath, string Directory) WriteSkill(string directoryName, string content)
    {
        var dir = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(file, content);
        return (file, dir);
    }

    [Fact]
    public void ParseFromFile_FrontmatterWithoutDescription_WarnsTheModelHasNothingToSelectItBy()
    {
        var (file, dir) = WriteSkill("no-description", "---\nname: no-description\n---\nBody.\n");

        var skill = _sut.ParseFromFile(file, dir);

        skill.Description.Should().BeEmpty("the skill still loads, as it always did");
        _logger.Logged(LogLevel.Warning, "no usable `description:`").Should().BeTrue();
    }

    [Fact]
    public void ParseFromFile_NoFrontmatterAtAll_DoesNotWarn()
    {
        var (file, dir) = WriteSkill("plain-markdown", "Just a body, no frontmatter.\n");

        _sut.ParseFromFile(file, dir);

        _logger.Logged(LogLevel.Warning, "no usable `description:`").Should().BeFalse(
            "a pure-markdown SKILL.md is a legitimate manifest that declares nothing");
    }

    [Fact]
    public void ParseFromFile_WhitespaceOnlyDescription_Warns()
    {
        var (file, dir) = WriteSkill("blank-description", "---\nname: blank-description\ndescription: \"   \"\n---\nBody.\n");

        _sut.ParseFromFile(file, dir);

        _logger.Logged(LogLevel.Warning, "no usable `description:`").Should().BeTrue();
    }

    [Fact]
    public void ParseFromFile_NameDiffersFromDirectory_KeepsTheFrontmatterNameAsIdWithoutWarning()
    {
        var (file, dir) = WriteSkill("the-directory", "---\nname: the-declared-name\ndescription: A skill.\n---\nBody.\n");

        var skill = _sut.ParseFromFile(file, dir);

        skill.Id.Should().Be("the-declared-name");
        _logger.Logged(LogLevel.Warning, "Skill ").Should().BeFalse();
    }

    [Fact]
    public void ParseFromFile_NameMatchesDirectoryAndDescriptionPresent_DoesNotWarn()
    {
        var (file, dir) = WriteSkill("well-formed", "---\nname: well-formed\ndescription: A skill.\n---\nBody.\n");

        _sut.ParseFromFile(file, dir);

        _logger.Logged(LogLevel.Warning, "Skill ").Should().BeFalse();
    }
}
