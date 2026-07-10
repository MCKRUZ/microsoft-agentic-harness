using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.Permissions;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Permissions;

/// <summary>
/// Tests the capability-envelope rule provider — the enforcement half of the per-caller grant. It is inert
/// off the bundle path (no ambient envelope) and, when an envelope is active, emits bypass-immune Deny for
/// the tools a bundle declares but is not granted plus an autonomy-ceiling baseline for the granted tools.
/// The tests set the ambient envelope and overlay directly, exactly how a bundle run will at runtime.
/// </summary>
public sealed class EnvelopePermissionRuleProviderTests
{
    private readonly EnvelopePermissionRuleProvider _provider = new();

    [Fact]
    public void Source_IsCapabilityEnvelope()
        => _provider.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);

    [Fact]
    public async Task NoAmbientEnvelope_EmitsNoRules()
    {
        // Off the bundle path the provider must be completely silent — no rule can leak into a normal turn.
        var rules = await _provider.GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task DeclaredToolOutsideEnvelope_EmitsBypassImmuneDeny()
    {
        var overlay = Overlay(Agent("bundle", "file_system", "bash"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["file_system"])))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            var deny = rules.Should().ContainSingle(r => r.Behavior == PermissionBehaviorType.Deny).Subject;
            deny.ToolPattern.Should().Be("bash", "the declared tool the envelope does not grant is denied by name");
            deny.IsBypassImmune.Should().BeTrue("an out-of-envelope deny cannot be lifted by any auto-approve mode");
            deny.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Fact]
    public async Task GrantedTool_IsNotDenied_AndGetsCeilingBaseline()
    {
        var overlay = Overlay(Agent("bundle", "file_system"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["file_system"], ceiling: AutonomyLevel.Supervised)))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            rules.Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
            var baseline = rules.Should().ContainSingle(r => r.IsAuthoritativeBaseline).Subject;
            baseline.ToolPattern.Should().Be("file_system");
            baseline.Behavior.Should().Be(PermissionBehaviorType.Ask, "Supervised caps autonomy at approval-required");
        }
    }

    [Theory]
    [InlineData(AutonomyLevel.Autonomous, PermissionBehaviorType.Allow)]
    [InlineData(AutonomyLevel.Supervised, PermissionBehaviorType.Ask)]
    [InlineData(AutonomyLevel.Restricted, PermissionBehaviorType.Ask)]
    public async Task CeilingBaseline_MapsAutonomyToBehavior(AutonomyLevel ceiling, PermissionBehaviorType expected)
    {
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["t"], ceiling: ceiling)))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            rules.Should().ContainSingle(r => r.IsAuthoritativeBaseline)
                .Which.Behavior.Should().Be(expected);
        }
    }

    [Fact]
    public async Task EmptyEnvelope_DeniesEveryDeclaredTool_AndGrantsNoBaseline()
    {
        // A deny-all envelope (grants nothing) is the fail-closed default. Every tool the bundle declares
        // is denied bypass-immune, and there is no allow/baseline for anything.
        var overlay = Overlay(Agent("bundle", "file_system", "bash"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope()))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            rules.Should().OnlyContain(r => r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
            rules.Select(r => r.ToolPattern).Should().BeEquivalentTo(["file_system", "bash"]);
        }
    }

    [Fact]
    public async Task EnvelopeGrantMatchesToolCaseInsensitively_NoDeny()
    {
        // The bundle declares "file_system"; the envelope grants "File_System". A case-sensitive check would
        // wrongly deny the granted tool — the envelope's grant is case-insensitive, so it must not.
        var overlay = Overlay(Agent("bundle", "file_system"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["File_System"])))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            rules.Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
        }
    }

    [Fact]
    public async Task DeclaredToolsPulledFromOwnedSkills_Denied_WhenOutsideEnvelope()
    {
        // Tools the bundle declares live on the agent's ceiling AND on each owned skill; the provider must
        // consider both when computing the out-of-envelope deny set.
        var overlay = new EphemeralAgentOverlay
        {
            Agent = Agent("bundle"),
            OwnedSkills =
            [
                new SkillDefinition { Id = "s1", Name = "s1", AllowedTools = ["skill_tool"] },
                new SkillDefinition
                {
                    Id = "s2", Name = "s2",
                    ToolDeclarations = [new ToolDeclaration { Name = "declared_tool" }]
                }
            ]
        };

        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["skill_tool"])))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            rules.Where(r => r.Behavior == PermissionBehaviorType.Deny).Select(r => r.ToolPattern)
                .Should().BeEquivalentTo(["declared_tool"], "only the skill tool outside the envelope is denied");
        }
    }

    [Fact]
    public async Task OverlayForDifferentAgent_ContributesNoDenySet_ButStillCapsAutonomy()
    {
        // If the resolved agent is not the one the overlay owns, the provider can't enumerate its declared
        // tools, so it emits no deny set — but the envelope's autonomy ceiling still applies to granted tools
        // (the fail-closed default handles anything else at runtime).
        var overlay = Overlay(Agent("bundle", "bash"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["file_system"], ceiling: AutonomyLevel.Supervised)))
        {
            var rules = await _provider.GetRulesAsync("some-other-agent");

            rules.Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
            rules.Should().ContainSingle(r => r.IsAuthoritativeBaseline)
                .Which.ToolPattern.Should().Be("file_system");
        }
    }

    private static AgentDefinition Agent(string id, params string[] allowedTools) =>
        new() { Id = id, Name = id, AllowedTools = allowedTools };

    private static EphemeralAgentOverlay Overlay(AgentDefinition agent) =>
        new() { Agent = agent };

    private static CapabilityEnvelope Envelope(
        IReadOnlyList<string>? tools = null,
        AutonomyLevel ceiling = AutonomyLevel.Restricted) =>
        new() { AllowedTools = tools ?? [], AutonomyCeiling = ceiling };
}
