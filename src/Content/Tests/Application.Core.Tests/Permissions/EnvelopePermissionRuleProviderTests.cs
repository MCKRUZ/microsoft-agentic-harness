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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.Core.Tests.Permissions;

/// <summary>
/// Tests the capability-envelope rule provider — the enforcement half of the per-caller grant. It is inert
/// off the bundle path (no ambient envelope) and, when an envelope is active, emits bypass-immune Deny for
/// the tools a bundle declares but is not granted, an autonomy-ceiling baseline for the granted tools, and
/// one closing catch-all Deny that makes the allowlist a closed set. The tests set the ambient envelope and
/// overlay directly, exactly how a bundle run will at runtime.
/// </summary>
public sealed class EnvelopePermissionRuleProviderTests
{
    private readonly EnvelopePermissionRuleProvider _provider =
        new(NullLogger<EnvelopePermissionRuleProvider>.Instance);

    /// <summary>
    /// The rules written for a specific tool name, i.e. everything except the closing catch-all. Most
    /// assertions here are about the per-tool rules; the catch-all has its own dedicated tests.
    /// </summary>
    private static IEnumerable<ToolPermissionRule> PerTool(IEnumerable<ToolPermissionRule> rules)
        => rules.Where(r => r.ToolPattern != "*");

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

            var deny = PerTool(rules).Should()
                .ContainSingle(r => r.Behavior == PermissionBehaviorType.Deny).Subject;
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

            PerTool(rules).Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
            var baseline = PerTool(rules).Should().ContainSingle(r => r.IsAuthoritativeBaseline).Subject;
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

            PerTool(rules).Should().ContainSingle(r => r.IsAuthoritativeBaseline)
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

            PerTool(rules).Should().OnlyContain(r => r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
            PerTool(rules).Select(r => r.ToolPattern).Should().BeEquivalentTo(["file_system", "bash"]);
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

            PerTool(rules).Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
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

            PerTool(rules).Where(r => r.Behavior == PermissionBehaviorType.Deny).Select(r => r.ToolPattern)
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

            PerTool(rules).Should().NotContain(r => r.Behavior == PermissionBehaviorType.Deny);
            PerTool(rules).Should().ContainSingle(r => r.IsAuthoritativeBaseline)
                .Which.ToolPattern.Should().Be("file_system");
        }
    }

    [Fact]
    public async Task ActiveEnvelope_EmitsClosingCatchAllDeny()
    {
        // The rule that makes the allowlist a CLOSED set. It must be a baseline (so it is arbitrated in
        // phase 1.5 by specificity rather than swallowing the grants in the phase-1b Deny scan) and sit at
        // the lowest possible precedence. Without it an ungranted name matches nothing here and falls
        // through to the host's generic autonomy tier, which ships as "* Allow" on both bundle hosts.
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["file_system"])))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            var closing = rules.Should().ContainSingle(r => r.ToolPattern == "*").Subject;
            closing.Behavior.Should().Be(PermissionBehaviorType.Deny);
            closing.IsAuthoritativeBaseline.Should().BeTrue(
                "a plain Deny would match in phase 1b and deny the granted tools too");
            closing.Priority.Should().Be(int.MaxValue, "every other baseline must outrank it on a tie");
            closing.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Fact]
    public async Task WildcardGrant_IsRejected_AndGrantsNothing()
    {
        // An operator writing "*" to mean "all tools" must not silently obtain the reserved plan
        // capabilities. The entry is dropped, so no baseline is emitted for it and only the closing Deny
        // remains — the run is confined to the grants that were actually spelled out (here, none).
        using (CapabilityEnvelopeAccessor.Begin(Envelope(tools: ["*", "file_*", "file_system"])))
        {
            var rules = await _provider.GetRulesAsync("bundle");

            PerTool(rules).Select(r => r.ToolPattern).Should().BeEquivalentTo(
                ["file_system"],
                "only the exact-name grant survives; wildcard entries are rejected");
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
