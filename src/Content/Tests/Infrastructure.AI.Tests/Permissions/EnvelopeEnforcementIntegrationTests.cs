using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.Permissions;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Planner;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

/// <summary>
/// End-to-end enforcement of the capability envelope through the <em>real</em>
/// <see cref="ThreePhasePermissionResolver"/>, the real glob matcher, and the <em>full production set of
/// rule providers</em> — envelope, autonomy tier, config, and plugin — wired exactly as
/// <c>Application.Core</c> and <c>Infrastructure.AI</c> register them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the whole provider set and not just the envelope provider.</strong> These tests previously
/// drove the resolver with the envelope provider alone. That rule set does not exist in any deployment,
/// and it hid a real hole: with the other providers present, a tool the envelope never granted matched no
/// envelope rule, fell through to the autonomy tier's catch-all, and — under the configuration both
/// bundle-capable hosts actually ship (<c>DefaultBehavior: Allow</c>, <c>DefaultAutonomyLevel:
/// Autonomous</c>) — resolved to <b>Allow</b>. The envelope was documented fail-closed while behaving
/// fail-open. A confinement test is only meaningful against the rule set production assembles, so the
/// configuration here is copied from the shipped hosts rather than chosen to make assertions pass.
/// </para>
/// <para>
/// <see cref="TierPolicyConfig"/> mirrors <c>Presentation.FoundryHost</c> (which defines
/// <c>TierPolicies</c> with <c>Autonomous → Allow</c>) and <see cref="FlatConfig"/> mirrors
/// <c>Presentation.BundleApi</c> (which defines none, so the flat <c>DefaultBehavior</c> applies). Both
/// resolve the tier catch-all to Allow by different routes, so the confinement assertions run against
/// both.
/// </para>
/// </remarks>
public sealed class EnvelopeEnforcementIntegrationTests
{
    private const string AgentId = "bundle";

    private readonly Mock<ISafetyGateRegistry> _safetyGates = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly GlobPatternMatcher _matcher = new();

    public EnvelopeEnforcementIntegrationTests()
    {
        _safetyGates
            .Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);
    }

    /// <summary>
    /// The permission configuration <c>Presentation.BundleApi</c> ships: a flat Allow default and an
    /// Autonomous default tier, with no per-tier policy block.
    /// </summary>
    private static PermissionsConfig FlatConfig() => new()
    {
        DefaultBehavior = "Allow",
        DefaultAutonomyLevel = "Autonomous",
        DenialRateLimitThreshold = 5
    };

    /// <summary>
    /// The permission configuration <c>Presentation.FoundryHost</c> ships: the same flat defaults plus a
    /// <c>TierPolicies</c> block whose Autonomous tier also defaults to Allow.
    /// </summary>
    private static PermissionsConfig TierPolicyConfig()
    {
        var config = FlatConfig();
        config.TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
        {
            ["Restricted"] = new() { DefaultBehavior = "Ask" },
            ["Supervised"] = new() { DefaultBehavior = "Ask" },
            ["Autonomous"] = new() { DefaultBehavior = "Allow" }
        };
        return config;
    }

    public static TheoryData<bool> ShippedHostConfigurations => new() { false, true };

    /// <summary>
    /// Builds the resolver over the full production provider list, in the order
    /// <c>Application.Core.DependencyInjection</c> and
    /// <c>Infrastructure.AI.DependencyInjection.Governance</c> register them.
    /// </summary>
    /// <param name="permissions">The permission configuration the host is running under.</param>
    private ThreePhasePermissionResolver Resolver(PermissionsConfig permissions)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Permissions = permissions } };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        // Agent ids that are not SubagentType names fall to DefaultAutonomyLevel, so the tier resolver is
        // never consulted for "bundle" — it is present because production wires it.
        var tierResolver = new Mock<IAutonomyTierResolver>();
        tierResolver.Setup(r => r.Resolve(It.IsAny<SubagentType>())).Returns(AutonomyLevel.Autonomous);

        var pluginRegistry = new Mock<IPluginRegistry>();
        pluginRegistry.Setup(r => r.GetLoadedPlugins()).Returns([]);

        var skillRegistry = new Mock<ISkillMetadataRegistry>();
        skillRegistry.Setup(r => r.GetAll()).Returns([]);

        IPermissionRuleProvider[] providers =
        [
            new AutonomyTierRuleProvider(
                tierResolver.Object, options, NullLogger<AutonomyTierRuleProvider>.Instance),
            new PluginPermissionRuleProvider(
                pluginRegistry.Object, skillRegistry.Object, new ServiceCollection().BuildServiceProvider(),
                NullLogger<PluginPermissionRuleProvider>.Instance),
            new EnvelopePermissionRuleProvider(NullLogger<EnvelopePermissionRuleProvider>.Instance),
            new ConfigBasedRuleProvider(options)
        ];

        return new ThreePhasePermissionResolver(
            providers,
            _safetyGates.Object,
            _matcher,
            _denialTracker.Object,
            options,
            NullLogger<ThreePhasePermissionResolver>.Instance);
    }

    [Theory]
    [MemberData(nameof(ShippedHostConfigurations))]
    public async Task UndeclaredOutOfEnvelopeTool_IsDenied(bool useTierPolicies)
    {
        // THE REGRESSION TEST. A tool the bundle never declared (so no per-name Deny is emitted) and the
        // envelope never granted must be DENIED, not merely left to a default. Before the closing
        // catch-all existed this resolved to Allow via the tier's "*" rule under exactly this config.
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolve(useTierPolicies, "surprise_tool");

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny,
                "the envelope is an allowlist — anything it did not grant is outside the grant");
            decision.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope,
                "the denial must come from the envelope's closing rule, not from a permissive tier default");
        }
    }

    [Theory]
    [InlineData(PlanCapabilities.LlmCall)]
    [InlineData(PlanCapabilities.Retrieval)]
    public async Task UngrantedReservedPlanCapability_IsDenied(string capability)
    {
        // The two capabilities this whole confinement exists to gate. An envelope that grants a tool but
        // not inference/retrieval must not buy the caller unbounded tokens or corpus access through the
        // host's permissive tier default.
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolve(useTierPolicies: false, capability);

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
            decision.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Theory]
    [InlineData(PlanCapabilities.LlmCall)]
    [InlineData(PlanCapabilities.Retrieval)]
    public async Task WildcardGrant_DoesNotConferReservedPlanCapabilities(string capability)
    {
        // An operator writing "*" meaning "all tools" must not silently obtain inference and retrieval.
        // The entry is rejected, so the envelope still grants nothing and the closing rule denies.
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["*"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolve(useTierPolicies: false, capability);

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        }
    }

    [Theory]
    [MemberData(nameof(ShippedHostConfigurations))]
    public async Task GrantedTool_AutonomousCeiling_ResolvesToAllow(bool useTierPolicies)
    {
        // The other half of the guarantee: closing the envelope must not break what it DID grant. The
        // per-name baseline is more specific than the closing "*" Deny and therefore outranks it.
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolve(useTierPolicies, "file_system");

            decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
            decision.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Fact]
    public async Task GrantedTool_SupervisedCeiling_ResolvesToAsk()
    {
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Supervised)))
        {
            var decision = await Resolve(useTierPolicies: false, "file_system");

            decision.Behavior.Should().Be(PermissionBehaviorType.Ask,
                "the ceiling still caps a granted tool — the closing rule must not override it either way");
        }
    }

    [Fact]
    public async Task OutOfEnvelopeDeclaredTool_ResolvesToBypassImmuneDeny()
    {
        // A DECLARED but ungranted tool is denied by its own bypass-immune rule rather than by the
        // closing catch-all, so the denial stays attributable and cannot be lifted by auto-approve modes.
        var overlay = Overlay("file_system", "bash");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolve(useTierPolicies: false, "bash");

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
            decision.MatchedRule!.IsBypassImmune.Should().BeTrue();
            decision.MatchedRule.ToolPattern.Should().Be("bash", "the named rule wins over the catch-all");
            decision.MatchedRule.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Theory]
    [MemberData(nameof(ShippedHostConfigurations))]
    public async Task NoEnvelope_InProcessPath_IsUnchanged(bool useTierPolicies)
    {
        // No ambient envelope: the provider contributes nothing, so resolution is governed entirely by
        // the host's own tier configuration exactly as it was before the envelope existed. This is the
        // guarantee that the closing Deny is scoped to bundle runs and cannot deny in-process callers.
        var decision = await Resolve(useTierPolicies, "anything_at_all");

        decision.Behavior.Should().Be(PermissionBehaviorType.Allow,
            "an Allow default tier still governs the non-bundle path");
        decision.Source.Should().Be(PermissionRuleSource.AutonomyTier,
            "no capability-envelope rule may participate off the bundle path");
    }

    [Fact]
    public async Task NoEnvelope_RestrictiveHost_StillAsks()
    {
        // The same pass-through property under a host that has NOT opted into a permissive default:
        // the envelope provider neither tightens nor loosens the in-process path.
        var restrictive = new PermissionsConfig { DenialRateLimitThreshold = 5 };
        var decision = await Resolver(restrictive).ResolvePermissionAsync(AgentId, "anything_at_all");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
    }

    private async Task<PermissionDecision> Resolve(bool useTierPolicies, string toolName)
        => await Resolver(useTierPolicies ? TierPolicyConfig() : FlatConfig())
            .ResolvePermissionAsync(AgentId, toolName);

    private static EphemeralAgentOverlay Overlay(params string[] declaredTools) => new()
    {
        Agent = new AgentDefinition { Id = AgentId, Name = AgentId, AllowedTools = declaredTools }
    };

    private static CapabilityEnvelope Envelope(IReadOnlyList<string> tools, AutonomyLevel ceiling) =>
        new() { AllowedTools = tools, AutonomyCeiling = ceiling };
}
