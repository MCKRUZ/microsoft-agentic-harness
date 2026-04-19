using FluentAssertions;
using Infrastructure.AI.Skills;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="CandidateSkillContentProvider"/> covering snapshot-based
/// content serving, case-insensitive lookup, and missing path handling.
/// </summary>
public sealed class CandidateSkillContentProviderTests
{
    [Fact]
    public async Task GetSkillContentAsync_ExistingPath_ReturnsContent()
    {
        var snapshots = new Dictionary<string, string>
        {
            ["/skills/review/SKILL.md"] = "# Review Skill\n\nInstructions here."
        };

        var provider = new CandidateSkillContentProvider(snapshots);

        var content = await provider.GetSkillContentAsync("/skills/review/SKILL.md");

        content.Should().Be("# Review Skill\n\nInstructions here.");
    }

    [Fact]
    public async Task GetSkillContentAsync_MissingPath_ReturnsNull()
    {
        var snapshots = new Dictionary<string, string>
        {
            ["/skills/review/SKILL.md"] = "content"
        };

        var provider = new CandidateSkillContentProvider(snapshots);

        var content = await provider.GetSkillContentAsync("/skills/missing/SKILL.md");

        content.Should().BeNull();
    }

    [Fact]
    public async Task GetSkillContentAsync_CaseInsensitiveLookup_Matches()
    {
        var snapshots = new Dictionary<string, string>
        {
            ["C:\\Skills\\Review\\SKILL.md"] = "Windows path content"
        };

        var provider = new CandidateSkillContentProvider(snapshots);

        var content = await provider.GetSkillContentAsync("c:\\skills\\review\\skill.md");

        content.Should().Be("Windows path content");
    }

    [Fact]
    public async Task GetSkillContentAsync_EmptySnapshots_ReturnsNull()
    {
        var provider = new CandidateSkillContentProvider(new Dictionary<string, string>());

        var content = await provider.GetSkillContentAsync("/any/path");

        content.Should().BeNull();
    }

    [Fact]
    public void Constructor_NullSnapshots_Throws()
    {
        var act = () => new CandidateSkillContentProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetSkillContentAsync_MultipleSnapshots_ReturnsCorrectOne()
    {
        var snapshots = new Dictionary<string, string>
        {
            ["/skills/a/SKILL.md"] = "Skill A",
            ["/skills/b/SKILL.md"] = "Skill B",
            ["/skills/c/SKILL.md"] = "Skill C"
        };

        var provider = new CandidateSkillContentProvider(snapshots);

        var contentA = await provider.GetSkillContentAsync("/skills/a/SKILL.md");
        var contentC = await provider.GetSkillContentAsync("/skills/c/SKILL.md");

        contentA.Should().Be("Skill A");
        contentC.Should().Be("Skill C");
    }
}
