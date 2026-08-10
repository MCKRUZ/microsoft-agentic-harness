using Infrastructure.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Loads the real, live <c>skills/echo-test/SKILL.md</c> and confirms it is shaped the way
/// <c>AgentExecutionContextFactory.ResolveFrameworkTypeFromMetadata</c> actually reads it.
/// </summary>
/// <remarks>
/// Regression coverage for issue #355. <c>echo-test</c>'s manifest previously declared
/// <c>framework_type: "Echo"</c> at the top level of its frontmatter — a shape
/// <c>SkillMetadataParser</c> never promotes into <see cref="Domain.AI.Skills.SkillDefinition.Metadata"/>,
/// which only ever comes from a nested <c>metadata:</c> block. The field was therefore silent dead
/// weight: it read as a declared capability but never routed the agent to the deterministic
/// <c>EchoChatClient</c> its own description promises ("without requiring an external LLM"), instead
/// falling through to whatever chat client the host is globally configured with. A unit test against
/// a synthetic <see cref="Domain.AI.Skills.SkillDefinition"/>
/// (<c>AgentExecutionContextFactoryTests.MapToAgentContext_SkillMetadataFrameworkType_UsedWhenNoOverride</c>)
/// proves the resolution mechanism itself works; this test proves the actual shipped manifest file is
/// shaped to trigger it — a mechanism test alone would stay green even if the real SKILL.md drifted
/// back to the flat, unread shape.
/// </remarks>
public sealed class EchoTestSkillManifestTests
{
    [Fact]
    public void EchoTestSkillManifest_FrameworkTypeIsNestedUnderMetadata_SoItIsActuallyRead()
    {
        var parser = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader());
        var skillPath = RepoRoot.Combine("skills", "echo-test");
        var filePath = Path.Combine(skillPath, "SKILL.md");

        var skill = parser.ParseFromFile(filePath, skillPath);

        skill.Metadata.Should().NotBeNull(
            "framework_type must live under a nested metadata: block, not at the top level, " +
            "or ResolveFrameworkTypeFromMetadata never sees it");
        skill.Metadata!.Should().ContainKey("framework_type")
            .WhoseValue.Should().Be("Echo");
    }
}
