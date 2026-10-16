using FluentAssertions;
using Infrastructure.AI.Skills;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// Pins the shipped <c>harness-proposer</c> skill's tool grant (#847). The MetaHarness proposer reads agent
/// traces, which are text other runs produced, so it must hold a tool that cannot write. A skill's declared
/// operations are not enforced when a tool is called, so the only dependable control is which tool the skill
/// is granted: this fails if <c>file_system</c> (or anything else that can write) is added back.
/// </summary>
public sealed class HarnessProposerDefinitionTests
{
    private static readonly string SkillFile = RepoRoot.Combine("skills", "harness-proposer", "SKILL.md");

    [Fact]
    public void Skill_IsGrantedTheReadOnlyEvidenceToolAndNothingElse()
    {
        var parser = new SkillMetadataParser(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillMetadataParser>.Instance,
            new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(),
            TestMcpSecurityScanner.DefaultConfig(),
            TestMcpSecurityScanner.RealEgressValidator());

        var skill = parser.ParseFromFile(SkillFile, Path.GetDirectoryName(SkillFile)!);

        skill.AllowedTools.Should().Equal("evidence_reader");
    }
}
