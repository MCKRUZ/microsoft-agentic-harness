using Application.AI.Common.Helpers;
using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Tests <see cref="FrameworkSkillCoverage"/>, the predicate that decides whether a skill's body may be
/// left out of the static system prompt and fetched on demand instead.
/// </summary>
/// <remarks>
/// Each test names the loader rule it encodes. The asymmetry matters throughout: a false negative costs
/// tokens (the body stays in the prompt unnecessarily) while a false positive costs the agent its
/// instructions with no error at all, so every ambiguous case must resolve to "not disclosable".
/// </remarks>
public sealed class FrameworkSkillCoverageTests : IDisposable
{
    private readonly SkillDirectoryFixture _skills = new("coverage");

    public void Dispose() => _skills.Dispose();

    private static SkillDefinition Skill(string id, string name, string baseDirectory) =>
        new() { Id = id, Name = name, BaseDirectory = baseDirectory };

    // ── the happy path ───────────────────────────────────────────────────────

    [Fact]
    public void SkillDirectoryWiredDirectly_IsDisclosable()
    {
        var dir = _skills.CreateSkill("demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEquivalentTo(["demo"]);
    }

    [Fact]
    public void SkillOneLevelUnderWiredRoot_IsDisclosable()
    {
        var dir = _skills.CreateSkill("demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [_skills.Root]);

        result.Should().BeEquivalentTo(["demo"]);
    }

    // ── rule: frontmatter name must equal the directory name (ordinal) ───────

    [Fact]
    public void FrontmatterNameDiffersFromDirectoryName_IsNotDisclosable()
    {
        // The loader rejects this skill outright, so its body must stay in the prompt.
        var dir = _skills.CreateSkillWithFrontmatter("demo", "name: something-else\ndescription: A skill.");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FrontmatterNameDiffersFromDirectoryNameOnlyByCase_IsNotDisclosable()
    {
        // The loader compares ordinally. Treating a case-only difference as a match is precisely how a
        // skill silently loses its instructions.
        var dir = _skills.CreateSkillWithFrontmatter("Demo", "name: demo\ndescription: A skill.");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty();
    }

    // ── rule: the frontmatter itself must satisfy the framework's validators ─

    [Fact]
    public void ManifestWithoutNameKey_IsNotDisclosable()
    {
        // SkillMetadataParser defaults a missing name to the directory name, so SkillDefinition.Name
        // looks like a match. The framework requires the name in the FILE and rejects this skill — which
        // is why coverage re-reads the frontmatter instead of trusting the parsed value.
        var dir = _skills.CreateSkillWithFrontmatter("demo", "description: A skill.");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty("the parser's defaulted name must not be mistaken for a declared one");
    }

    [Fact]
    public void ManifestWithoutDescriptionKey_IsNotDisclosable()
    {
        // Same shape as the missing name: the parser defaults description to empty, the loader rejects.
        var dir = _skills.CreateSkillWithFrontmatter("demo", "name: demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ManifestNameViolatingTheSpecCharset_IsNotDisclosable()
    {
        // The Agent Skills spec allows lowercase letters, digits and single hyphens only. An underscore
        // matches the directory name but fails the framework's own validator.
        var dir = _skills.CreateSkillWithFrontmatter("demo_skill", "name: demo_skill\ndescription: A skill.");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo_skill", "demo_skill", dir)], [dir]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ManifestWithoutFrontmatterBlock_IsNotDisclosable()
    {
        var dir = _skills.CreateSkillWithoutFrontmatter("demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void QuotedFrontmatterValues_AreAccepted()
    {
        // Shipped manifests quote their values; the loader strips the quotes and so must this check.
        var dir = _skills.CreateSkillWithFrontmatter("demo", "name: \"demo\"\ndescription: \"A skill.\"");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEquivalentTo(["demo"]);
    }

    // ── rule: a SKILL.md must actually be there ──────────────────────────────

    [Fact]
    public void DirectoryWithoutSkillFile_IsNotDisclosable()
    {
        var dir = _skills.CreateEmptyDirectory("demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [dir]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MissingDirectory_IsNotDisclosable()
    {
        var dir = Path.Combine(_skills.Root, "does-not-exist");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [_skills.Root]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SkillWithEmptyDirectory_IsNotDisclosable()
    {
        // In-memory and synthesized skills have no directory, so nothing can serve them on demand.
        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", string.Empty)], [_skills.Root]);

        result.Should().BeEmpty();
    }

    // ── rule: the loader searches only two levels below a root ───────────────

    [Fact]
    public void SkillAtSearchDepthLimit_IsDisclosable()
    {
        var dir = _skills.CreateSkill(Path.Combine("a", "demo"));

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [_skills.Root]);

        result.Should().BeEquivalentTo(["demo"]);
    }

    [Fact]
    public void SkillBeyondSearchDepth_IsNotDisclosable()
    {
        // The harness registry searches one level deeper than the framework loader, so a skill can be
        // discovered locally and still be invisible to the provider.
        var dir = _skills.CreateSkill(Path.Combine("a", "b", "demo"));

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [_skills.Root]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SkillOutsideEveryWiredRoot_IsNotDisclosable()
    {
        var dir = _skills.CreateSkill("demo");
        var unrelatedRoot = _skills.CreateEmptyDirectory("elsewhere");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [unrelatedRoot]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SiblingRootWithSharedPrefix_DoesNotCountAsContaining()
    {
        // "…/skills-extra" must not be treated as living under "…/skills".
        var dir = _skills.CreateSkill(Path.Combine("skills-extra", "demo"));
        var skillsRoot = _skills.CreateEmptyDirectory("skills");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], [skillsRoot]);

        result.Should().BeEmpty();
    }

    // ── no provider wired ────────────────────────────────────────────────────

    [Fact]
    public void NoWiredPaths_NothingIsDisclosable()
    {
        // No paths means no AgentSkillsProvider, so the eager merge is the only instruction source.
        var dir = _skills.CreateSkill("demo");

        var result = FrameworkSkillCoverage.SelectDisclosable([Skill("demo", "demo", dir)], []);

        result.Should().BeEmpty();
    }

    // ── mixed sets ───────────────────────────────────────────────────────────

    [Fact]
    public void MixedSkills_OnlyCoveredOnesAreSelected()
    {
        var covered = _skills.CreateSkill("covered");
        var tooDeep = _skills.CreateSkill(Path.Combine("a", "b", "deep"));

        var result = FrameworkSkillCoverage.SelectDisclosable(
            [
                Skill("covered", "covered", covered),
                Skill("deep", "deep", tooDeep),
                Skill("inmemory", "inmemory", string.Empty)
            ],
            [_skills.Root]);

        result.Should().BeEquivalentTo(["covered"]);
    }
}
