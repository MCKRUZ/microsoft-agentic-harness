using Application.AI.Common.Services.SkillTraining;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins <see cref="SkillTrainingEvidenceScope"/> (#843): a proposer session may read its own step's folder
/// and nothing else, including a sibling whose name merely starts the same way.
/// </summary>
public sealed class SkillTrainingEvidenceScopeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "scope-tests", "evidence");

    [Fact]
    public void Allows_WithNoFolderPublished_RefusesEverything()
    {
        SkillTrainingEvidenceScope.Current.Should().BeNull();

        SkillTrainingEvidenceScope.Allows(Path.Combine(Root, "step", "skill", "SKILL.md")).Should().BeFalse();
    }

    [Fact]
    public void Allows_TheFolderAndWhatIsInsideIt()
    {
        var step = Path.Combine(Root, "step1");
        using var _ = SkillTrainingEvidenceScope.Begin(step);

        SkillTrainingEvidenceScope.Allows(step).Should().BeTrue();
        SkillTrainingEvidenceScope.Allows(Path.Combine(step, "rollouts", "001-a.md")).Should().BeTrue();
        SkillTrainingEvidenceScope.Allows(step + Path.DirectorySeparatorChar).Should().BeTrue();
    }

    [Fact]
    public void Allows_ASiblingStepWhoseNameStartsTheSameWay_IsRefused()
    {
        // The trap in a prefix check: "step1" is a string prefix of "step10".
        using var _ = SkillTrainingEvidenceScope.Begin(Path.Combine(Root, "step1"));

        SkillTrainingEvidenceScope.Allows(Path.Combine(Root, "step10", "skill", "SKILL.md")).Should().BeFalse();
        SkillTrainingEvidenceScope.Allows(Path.Combine(Root, "step2", "skill", "SKILL.md")).Should().BeFalse();
        SkillTrainingEvidenceScope.Allows(Root).Should().BeFalse("the root holds every step's folder");
    }

    [Fact]
    public void Allows_ADotDotOutOfTheFolder_IsRefused()
    {
        using var _ = SkillTrainingEvidenceScope.Begin(Path.Combine(Root, "step1"));

        SkillTrainingEvidenceScope.Allows(Path.Combine(Root, "step1", "..", "step2", "skill", "SKILL.md")).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public void Allows_AnUnusablePath_IsRefusedNotThrown(string path)
    {
        using var _ = SkillTrainingEvidenceScope.Begin(Path.Combine(Root, "step1"));

        SkillTrainingEvidenceScope.Allows(path).Should().BeFalse();
    }

    [Fact]
    public void Dispose_RestoresWhatWasPublishedBefore()
    {
        var outer = Path.Combine(Root, "outer");
        using var first = SkillTrainingEvidenceScope.Begin(outer);
        using (SkillTrainingEvidenceScope.Begin(Path.Combine(Root, "inner")))
        {
            SkillTrainingEvidenceScope.Current.Should().EndWith("inner");
        }

        SkillTrainingEvidenceScope.Current.Should().Be(Path.GetFullPath(outer));
    }

    [Fact]
    public async Task Begin_IsVisibleToWorkRunningBelowIt()
    {
        var step = Path.Combine(Root, "step1");
        using var _ = SkillTrainingEvidenceScope.Begin(step);

        var below = await Task.Run(async () =>
        {
            await Task.Yield();
            return SkillTrainingEvidenceScope.Allows(Path.Combine(step, "a.md"));
        });

        below.Should().BeTrue("the tool runs far below the code that published the folder");
    }
}
