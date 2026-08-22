using Application.AI.Common.Interfaces;
using Application.AI.Common.Services.Governance;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for <see cref="ToolCallOncePolicy"/>: runtime registration/lookup, the safe defaults,
/// manifest seeding from <see cref="ISkillMetadataRegistry"/>, and that a discovery failure
/// degrades to an empty seed instead of poisoning every subsequent call.
/// </summary>
public sealed class ToolCallOncePolicyTests
{
    private static ToolCallOncePolicy CreatePolicy(ISkillMetadataRegistry? skillRegistry = null) =>
        new(NullLogger<ToolCallOncePolicy>.Instance, skillRegistry);

    private static SkillDefinition Skill(string id, params ToolDeclaration[] tools) =>
        Skill(id, pluginSource: null, tools);

    private static SkillDefinition Skill(string id, string? pluginSource, params ToolDeclaration[] tools) => new()
    {
        Id = id,
        Name = id,
        Instructions = "Test",
        PluginSource = pluginSource,
        ToolDeclarations = [.. tools]
    };

    [Fact]
    public void IsCallOnce_UnregisteredTool_ReturnsFalse()
    {
        var policy = CreatePolicy();

        policy.IsCallOnce("never_registered").Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_RegisteredTool_ReturnsTrue()
    {
        var policy = CreatePolicy();

        policy.Register("start_diagnostic_session");

        policy.IsCallOnce("start_diagnostic_session").Should().BeTrue();
    }

    [Fact]
    public void IsCallOnce_IsCaseInsensitive()
    {
        var policy = CreatePolicy();

        policy.Register("Start_Diagnostic_Session");

        policy.IsCallOnce("start_diagnostic_session").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankName_IsANoOp(string? toolName)
    {
        var policy = CreatePolicy();

        policy.Register(toolName!);

        policy.IsCallOnce(toolName!).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCallOnce_BlankName_ReturnsFalse(string? toolName)
    {
        var policy = CreatePolicy();

        policy.IsCallOnce(toolName!).Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_NoRegistryComposed_FallsBackToRuntimeRegistrationOnly()
    {
        var policy = CreatePolicy(skillRegistry: null);

        policy.IsCallOnce("manifest_only_tool").Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_ToolDeclaredCallOnceInManifest_ReturnsTrueWithoutRuntimeRegistration()
    {
        // The bug this closes: before manifest seeding, a host serving only the direct-invoke or
        // workflow-run surfaces (neither of which goes through ToolChainBuilder) never populated
        // this policy at all, so "call-once" was declared in config and silently unenforced.
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(
        [
            Skill("diagnostics", new ToolDeclaration
            {
                Name = "start_diagnostic_session",
                CallOncePerConversation = true
            })
        ]);
        var policy = CreatePolicy(registry.Object);

        policy.IsCallOnce("start_diagnostic_session").Should().BeTrue();
    }

    [Fact]
    public void IsCallOnce_ToolInManifestButNotDeclaredCallOnce_ReturnsFalse()
    {
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(
        [
            Skill("s", new ToolDeclaration { Name = "ordinary_tool" })
        ]);
        var policy = CreatePolicy(registry.Object);

        policy.IsCallOnce("ordinary_tool").Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_ManifestSeedIsComputedOnce_NotRePolledPerCall()
    {
        // Lazy, on first use — GetAll() must not be called once per IsCallOnce check.
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(
        [
            Skill("s", new ToolDeclaration { Name = "tool_a", CallOncePerConversation = true })
        ]);
        var policy = CreatePolicy(registry.Object);

        policy.IsCallOnce("tool_a").Should().BeTrue();
        policy.IsCallOnce("tool_a").Should().BeTrue();
        policy.IsCallOnce("tool_b").Should().BeFalse();

        registry.Verify(r => r.GetAll(), Times.Once);
    }

    [Fact]
    public void IsCallOnce_ToolDeclaredCallOnceByAPluginSourcedSkill_ManifestSeedSkipsIt()
    {
        // Security-review finding: a plugin-sourced skill's manifest cannot be trusted to speak for
        // whether its own plugin's AllowedTools/DeniedTools boundary would actually grant this tool —
        // that boundary is only ever consulted against a RESOLVED tool list, never a bare manifest
        // scan. Seeding this policy straight from the manifest would let a denied tool's name poison
        // the process-global, durably-unreleasable ledger before any conversation ever builds this
        // skill for real. See ToolChainBuilder.RegisterSurvivingCallOnceTools's remarks — that is the
        // one path that CAN verify the boundary, and this manifest seed must defer to it entirely.
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(
        [
            Skill("plugin-skill", pluginSource: "some-plugin", new ToolDeclaration
            {
                Name = "plugin_declared_tool",
                CallOncePerConversation = true
            })
        ]);
        var policy = CreatePolicy(registry.Object);

        policy.IsCallOnce("plugin_declared_tool").Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_ManifestDiscoveryThrows_DegradesToEmptySeed_NotOnEveryCall()
    {
        // The exact defect a naive Lazy<T> would reintroduce: its default mode caches an
        // exception from the factory and rethrows it on every later access, which would turn a
        // one-time manifest-discovery failure (an unregistered plugin registry, a malformed
        // SKILL.md) into every subsequent tool call in the process faulting. This must degrade to
        // "runtime-registration-only" instead, once, and stay that way.
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Throws(new InvalidOperationException("discovery failed"));
        var policy = CreatePolicy(registry.Object);

        var first = () => policy.IsCallOnce("any_tool");
        first.Should().NotThrow();
        policy.IsCallOnce("any_tool").Should().BeFalse();

        // Runtime registration must still work even though the manifest seed degraded.
        policy.Register("runtime_registered_tool");
        policy.IsCallOnce("runtime_registered_tool").Should().BeTrue();
    }
}
