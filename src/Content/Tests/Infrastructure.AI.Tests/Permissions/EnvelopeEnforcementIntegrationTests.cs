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
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Plugins;
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
    /// <remarks>
    /// <strong>The plugin registry is a real input, not scenery.</strong> Most cases here run with no
    /// plugins loaded, which is the shipped in-repo configuration — but "no plugins" is precisely the
    /// state in which the plugin provider contributes nothing and cannot contradict the envelope, so a
    /// suite that only ever passes an empty registry proves less about confinement than it appears to.
    /// <see cref="AutonomousPluginTool_OutsideTheEnvelope_IsDenied"/> supplies a loaded plugin so the
    /// second authoritative-baseline emitter is actually exercised.
    /// </remarks>
    /// <param name="permissions">The permission configuration the host is running under.</param>
    /// <param name="plugins">Loaded plugins the plugin provider should see. Empty unless a case needs one.</param>
    /// <param name="skills">Skill metadata the plugin provider reads tool names from.</param>
    private ThreePhasePermissionResolver Resolver(
        PermissionsConfig permissions,
        IReadOnlyList<LoadedPlugin>? plugins = null,
        IReadOnlyList<SkillDefinition>? skills = null)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Permissions = permissions } };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        // Agent ids that are not SubagentType names fall to DefaultAutonomyLevel, so the tier resolver is
        // never consulted for "bundle" — it is present because production wires it.
        var tierResolver = new Mock<IAutonomyTierResolver>();
        tierResolver.Setup(r => r.Resolve(It.IsAny<SubagentType>())).Returns(AutonomyLevel.Autonomous);

        var pluginRegistry = new Mock<IPluginRegistry>();
        pluginRegistry.Setup(r => r.GetLoadedPlugins()).Returns(plugins ?? []);

        var skillRegistry = new Mock<ISkillMetadataRegistry>();
        skillRegistry.Setup(r => r.GetAll()).Returns(skills ?? []);

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

    [Theory]
    [MemberData(nameof(ShippedHostConfigurations))]
    public async Task AutonomousPluginTool_OutsideTheEnvelope_IsDenied(bool useTierPolicies)
    {
        // THE SECOND-EMITTER REGRESSION TEST. PluginPermissionRuleProvider is the only other authoritative
        // baseline emitter, and for a plugin declaring AutonomyLevel: Autonomous it emits an EXACT-NAME
        // baseline Allow per declared tool. The envelope's closing rule is "*", which ranks lowest on
        // specificity — so before the grant-boundary tier existed the plugin's Allow won phase 1.5
        // outright and the bundle could invoke a tool the caller was never granted.
        //
        // The bundle's overlay declares only file_system, so EnumerateDeclaredTools never sees
        // k8sgpt_analyze and phase 1b emits no bypass-immune Deny for it. The resolver is the sole
        // enforcement point for tool names — ToolInvocationGovernor has no independent GrantsTool check —
        // so if this resolves to anything but Deny, the tool runs.
        var plugin = AutonomousPlugin("k8s-ops");
        var skill = PluginSkill("k8s-ops", "k8sgpt_analyze");

        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var resolver = Resolver(
                useTierPolicies ? TierPolicyConfig() : FlatConfig(), [plugin], [skill]);

            var decision = await resolver.ResolvePermissionAsync(AgentId, "k8sgpt_analyze");

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny,
                "a plugin's own autonomy declaration is a default within a grant, not a grant — it must " +
                "never widen the envelope to a tool the host did not authorise for this caller");
            decision.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope,
                "the denial must be attributable to the envelope's closing rule");
        }
    }

    [Fact]
    public async Task AutonomousPluginTool_InsideTheEnvelope_StillResolvesToAllow()
    {
        // The confinement must not degrade into "plugins are ignored". A plugin tool the envelope DOES
        // grant still auto-approves: the boundary caps authority, it does not veto everything outside its
        // own rule set. Without this, a passing Deny above would be indistinguishable from the boundary
        // simply swallowing all plugin baselines.
        var plugin = AutonomousPlugin("k8s-ops");
        var skill = PluginSkill("k8s-ops", "k8sgpt_analyze");

        var overlay = Overlay("k8sgpt_analyze");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["k8sgpt_analyze"], AutonomyLevel.Autonomous)))
        {
            var resolver = Resolver(FlatConfig(), [plugin], [skill]);

            var decision = await resolver.ResolvePermissionAsync(AgentId, "k8sgpt_analyze");

            decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        }
    }

    [Fact]
    public async Task RestrictivePluginBaseline_StillTightensAGrantedTool()
    {
        // The boundary is a CEILING, not a floor: it stops other providers widening past it but must not
        // stop them tightening within it. A plugin marked Restricted (baseline Ask) on a tool the envelope
        // granted with an Autonomous ceiling still forces approval. If the tier had been arbitrated as a
        // simple "boundary wins outright", this would wrongly resolve to Allow and the envelope's
        // documented "can only tighten, never loosen" ceiling semantics would be false.
        var plugin = Plugin("k8s-ops", autonomyLevel: "Restricted");
        var skill = PluginSkill("k8s-ops", "k8sgpt_analyze");

        var overlay = Overlay("k8sgpt_analyze");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["k8sgpt_analyze"], AutonomyLevel.Autonomous)))
        {
            var resolver = Resolver(FlatConfig(), [plugin], [skill]);

            var decision = await resolver.ResolvePermissionAsync(AgentId, "k8sgpt_analyze");

            decision.Behavior.Should().Be(PermissionBehaviorType.Ask,
                "a stricter peer baseline tightens within the envelope; only widening is refused");
        }
    }

    private async Task<PermissionDecision> Resolve(bool useTierPolicies, string toolName)
        => await Resolver(useTierPolicies ? TierPolicyConfig() : FlatConfig())
            .ResolvePermissionAsync(AgentId, toolName);

    private static LoadedPlugin AutonomousPlugin(string name) => Plugin(name, "Autonomous");

    /// <summary>
    /// A loaded plugin whose declaration sets <paramref name="autonomyLevel"/> — the manifest shape a
    /// consumer host writes, which no in-repo manifest currently uses.
    /// </summary>
    private static LoadedPlugin Plugin(string name, string autonomyLevel) => new(
        Name: name,
        Version: "1.0.0",
        LocalPath: $"/plugins/{name}",
        Manifest: new PluginManifest { Name = name, Version = "1.0.0" },
        Status: PluginLoadStatus.Loaded,
        SkillPaths: [],
        McpServerNames: [],
        Declaration: new PluginDeclaration { Name = name, AutonomyLevel = autonomyLevel });

    /// <summary>
    /// A skill attributed to <paramref name="pluginName"/> declaring <paramref name="toolName"/>. The
    /// plugin provider reads these to scope its autonomy baseline to real tool names.
    /// </summary>
    private static SkillDefinition PluginSkill(string pluginName, string toolName) => new()
    {
        Id = $"{pluginName}.skill",
        Name = $"{pluginName} skill",
        PluginSource = pluginName,
        AllowedTools = [toolName]
    };

    private static EphemeralAgentOverlay Overlay(params string[] declaredTools) => new()
    {
        Agent = new AgentDefinition { Id = AgentId, Name = AgentId, AllowedTools = declaredTools }
    };

    private static CapabilityEnvelope Envelope(IReadOnlyList<string> tools, AutonomyLevel ceiling) =>
        new() { AllowedTools = tools, AutonomyCeiling = ceiling };
}
