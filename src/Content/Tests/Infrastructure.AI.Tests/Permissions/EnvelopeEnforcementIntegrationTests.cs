using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.Permissions;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

/// <summary>
/// End-to-end enforcement of the capability envelope through the <em>real</em>
/// <see cref="ThreePhasePermissionResolver"/> and glob pattern matcher, driven only by the real
/// <see cref="EnvelopePermissionRuleProvider"/>. Proves the phase interaction the envelope relies on: an
/// out-of-envelope tool resolves to a bypass-immune Deny (phase 1b) that wins over the autonomy-ceiling
/// baseline (phase 1.5), and a granted tool resolves to exactly the ceiling behavior.
/// </summary>
public sealed class EnvelopeEnforcementIntegrationTests
{
    private const string AgentId = "bundle";

    private readonly Mock<ISafetyGateRegistry> _safetyGates = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly GlobPatternMatcher _matcher = new();
    private readonly IOptionsMonitor<AppConfig> _options;

    public EnvelopeEnforcementIntegrationTests()
    {
        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == new AppConfig
        {
            AI = new AIConfig { Permissions = new PermissionsConfig { DenialRateLimitThreshold = 5 } }
        });
        _safetyGates
            .Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);
    }

    private ThreePhasePermissionResolver Resolver() => new(
        [new EnvelopePermissionRuleProvider()],
        _safetyGates.Object,
        _matcher,
        _denialTracker.Object,
        _options,
        NullLogger<ThreePhasePermissionResolver>.Instance);

    [Fact]
    public async Task OutOfEnvelopeTool_ResolvesToBypassImmuneDeny()
    {
        var overlay = Overlay("file_system", "bash");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolver().ResolvePermissionAsync(AgentId, "bash");

            decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
            decision.MatchedRule!.IsBypassImmune.Should().BeTrue();
            decision.MatchedRule.Source.Should().Be(PermissionRuleSource.CapabilityEnvelope);
        }
    }

    [Fact]
    public async Task GrantedTool_AutonomousCeiling_ResolvesToAllow()
    {
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolver().ResolvePermissionAsync(AgentId, "file_system");

            decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        }
    }

    [Fact]
    public async Task GrantedTool_SupervisedCeiling_ResolvesToAsk()
    {
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Supervised)))
        {
            var decision = await Resolver().ResolvePermissionAsync(AgentId, "file_system");

            decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        }
    }

    [Fact]
    public async Task UndeclaredOutOfEnvelopeTool_FailsClosedToAsk()
    {
        // A tool the bundle never declared (so no explicit deny is emitted) and the envelope never granted
        // must still not resolve to Allow: with no matching allow/baseline the resolver defaults to Ask,
        // which the fail-closed governor turns into a denial.
        var overlay = Overlay("file_system");
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(Envelope(["file_system"], AutonomyLevel.Autonomous)))
        {
            var decision = await Resolver().ResolvePermissionAsync(AgentId, "surprise_tool");

            decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        }
    }

    [Fact]
    public async Task NoEnvelope_ProviderContributesNothing_DefaultsToAsk()
    {
        var decision = await Resolver().ResolvePermissionAsync(AgentId, "anything");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask, "with no rules at all the resolver defaults to Ask");
    }

    private static EphemeralAgentOverlay Overlay(params string[] declaredTools) => new()
    {
        Agent = new AgentDefinition { Id = AgentId, Name = AgentId, AllowedTools = declaredTools }
    };

    private static CapabilityEnvelope Envelope(IReadOnlyList<string> tools, AutonomyLevel ceiling) =>
        new() { AllowedTools = tools, AutonomyCeiling = ceiling };
}
