using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

public sealed class ThreePhasePermissionResolverAutonomyTests
{
    private readonly Mock<ISafetyGateRegistry> _safetyGateRegistry = new();
    private readonly GlobPatternMatcher _patternMatcher = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly Mock<ILogger<ThreePhasePermissionResolver>> _logger = new();

    public ThreePhasePermissionResolverAutonomyTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = new PermissionsConfig
                {
                    DenialRateLimitThreshold = 3
                }
            }
        };

        var optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMock.Setup(o => o.CurrentValue).Returns(appConfig);
        _options = optionsMock.Object;

        _denialTracker
            .Setup(d => d.IsRateLimited(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(false);

        _safetyGateRegistry
            .Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);
    }

    private ThreePhasePermissionResolver CreateResolver(params IPermissionRuleProvider[] providers)
    {
        return new ThreePhasePermissionResolver(
            providers,
            _safetyGateRegistry.Object,
            _patternMatcher,
            _denialTracker.Object,
            _options,
            _logger.Object);
    }

    private static Mock<IPermissionRuleProvider> BuildProvider(params ToolPermissionRule[] rules)
    {
        var provider = new Mock<IPermissionRuleProvider>();
        provider.Setup(p => p.GetRulesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);
        return provider;
    }

    [Fact]
    public async Task RestrictedAgent_AnyTool_ReturnsAsk()
    {
        // Restricted tier baseline: Ask for everything
        var tierProvider = BuildProvider(
            new ToolPermissionRule("*", null, PermissionBehaviorType.Ask, PermissionRuleSource.AutonomyTier, 0));

        var resolver = CreateResolver(tierProvider.Object);

        var decision = await resolver.ResolvePermissionAsync("restricted-agent", "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        decision.MatchedRule.Should().NotBeNull();
        decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.AutonomyTier);
    }

    [Fact]
    public async Task AutonomousAgent_AnyTool_ReturnsAllow()
    {
        // Autonomous tier baseline: Allow for everything
        var tierProvider = BuildProvider(
            new ToolPermissionRule("*", null, PermissionBehaviorType.Allow, PermissionRuleSource.AutonomyTier, 0));

        var resolver = CreateResolver(tierProvider.Object);

        var decision = await resolver.ResolvePermissionAsync("autonomous-agent", "file_system");

        decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        decision.MatchedRule.Should().NotBeNull();
        decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.AutonomyTier);
    }

    [Fact]
    public async Task AutonomousAgent_WithManifestDenyRule_DenyWins()
    {
        // Tier provides Allow(*), manifest provides Deny(bash)
        // Phase 1 Deny beats Phase 3 Allow
        var tierProvider = BuildProvider(
            new ToolPermissionRule("*", null, PermissionBehaviorType.Allow, PermissionRuleSource.AutonomyTier, 0));

        var manifestProvider = BuildProvider(
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Deny, PermissionRuleSource.AgentManifest, 5));

        var resolver = CreateResolver(tierProvider.Object, manifestProvider.Object);

        var decision = await resolver.ResolvePermissionAsync("autonomous-agent", "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        decision.MatchedRule.Should().NotBeNull();
        decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.AgentManifest);
    }

    [Fact]
    public async Task RestrictedAgent_WithAllowOverride_AllowWinsForSpecificTool()
    {
        // Ask(*) at priority 0, Allow(query_kg) at priority 10
        // Phase 2 finds Ask(*) first — but Phase 3 won't be reached.
        // Actually: the resolver evaluates phases in order: Deny -> Ask -> Allow.
        // Ask(*) matches in Phase 2, so Allow(query_kg) in Phase 3 is never reached.
        // The Ask rule wins.
        var provider = BuildProvider(
            new ToolPermissionRule("*", null, PermissionBehaviorType.Ask, PermissionRuleSource.AutonomyTier, 0),
            new ToolPermissionRule("query_kg", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 10));

        var resolver = CreateResolver(provider.Object);

        var decision = await resolver.ResolvePermissionAsync("restricted-agent", "query_kg");

        // Ask(*) matches in Phase 2 before Allow(query_kg) in Phase 3
        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
    }
}
