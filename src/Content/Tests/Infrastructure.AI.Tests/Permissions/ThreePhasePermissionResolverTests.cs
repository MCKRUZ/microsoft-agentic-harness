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

public sealed class ThreePhasePermissionResolverTests
{
    private readonly Mock<ISafetyGateRegistry> _safetyGateRegistry = new();
    private readonly GlobPatternMatcher _patternMatcher = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly Mock<ILogger<ThreePhasePermissionResolver>> _logger = new();

    public ThreePhasePermissionResolverTests()
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
    }

    private ThreePhasePermissionResolver CreateResolver(params ToolPermissionRule[] rules)
    {
        var provider = new Mock<IPermissionRuleProvider>();
        provider.Setup(p => p.GetRulesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

        _safetyGateRegistry.Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);

        return new ThreePhasePermissionResolver(
            [provider.Object],
            _safetyGateRegistry.Object,
            _patternMatcher,
            _denialTracker.Object,
            _options,
            _logger.Object);
    }

    [Fact]
    public async Task DenyRule_TakesPrecedence_OverAllowAndAsk()
    {
        var rules = new[]
        {
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 10),
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Ask, PermissionRuleSource.UserSettings, 5),
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Deny, PermissionRuleSource.PolicySettings, 1)
        };

        var resolver = CreateResolver(rules);

        var decision = await resolver.ResolvePermissionAsync("agent-1", "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        decision.MatchedRule.Should().NotBeNull();
        decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.PolicySettings);
    }

    [Fact]
    public async Task SafetyGate_TakesPrecedence_OverAllRules()
    {
        var rules = new[]
        {
            new ToolPermissionRule("*", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1)
        };

        var resolver = CreateResolver(rules);

        var gate = new SafetyGate(".git/", "Protected git directory");
        _safetyGateRegistry.Setup(r => r.CheckSafetyGate("file_system", It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(gate);

        var parameters = new Dictionary<string, object?> { ["path"] = "/repo/.git/config" };
        var decision = await resolver.ResolvePermissionAsync("agent-1", "file_system", parameters: parameters);

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        decision.Reason.Should().Contain("Safety gate");
        decision.Reason.Should().Contain(".git/");
    }

    [Fact]
    public async Task AskRule_ReturnedWhenNoDenyMatches()
    {
        var rules = new[]
        {
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Ask, PermissionRuleSource.UserSettings, 1),
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 2)
        };

        var resolver = CreateResolver(rules);

        var decision = await resolver.ResolvePermissionAsync("agent-1", "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
    }

    [Fact]
    public async Task AllowRule_ReturnedWhenNoAskOrDenyMatches()
    {
        var rules = new[]
        {
            new ToolPermissionRule("file_system", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1)
        };

        var resolver = CreateResolver(rules);

        var decision = await resolver.ResolvePermissionAsync("agent-1", "file_system");

        decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        decision.MatchedRule.Should().NotBeNull();
    }

    [Fact]
    public async Task NoMatchingRule_DefaultsToAsk()
    {
        var resolver = CreateResolver();

        var decision = await resolver.ResolvePermissionAsync("agent-1", "unknown_tool");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        decision.Reason.Should().Contain("No matching permission rule");
        decision.MatchedRule.Should().BeNull();
    }

    [Fact]
    public async Task IsToolAllowedAsync_ReturnsTrueOnlyForAllow()
    {
        var rules = new[]
        {
            new ToolPermissionRule("allowed_tool", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1),
            new ToolPermissionRule("denied_tool", null, PermissionBehaviorType.Deny, PermissionRuleSource.ProjectSettings, 1)
        };

        var resolver = CreateResolver(rules);

        (await resolver.IsToolAllowedAsync("agent-1", "allowed_tool", CancellationToken.None)).Should().BeTrue();
        (await resolver.IsToolAllowedAsync("agent-1", "denied_tool", CancellationToken.None)).Should().BeFalse();
        (await resolver.IsToolAllowedAsync("agent-1", "unknown_tool", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Rules_SortedByPriority()
    {
        var rules = new[]
        {
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Deny, PermissionRuleSource.ProjectSettings, 100),
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Deny, PermissionRuleSource.PolicySettings, 1)
        };

        var resolver = CreateResolver(rules);

        var decision = await resolver.ResolvePermissionAsync("agent-1", "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.PolicySettings);
        decision.MatchedRule.Priority.Should().Be(1);
    }

    [Fact]
    public async Task BypassImmune_AskRule_CannotBeOverridden()
    {
        var rules = new[]
        {
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Ask, PermissionRuleSource.PolicySettings, 1, IsBypassImmune: true),
            new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.SessionOverride, 2)
        };

        var resolver = CreateResolver(rules);

        var decision = await resolver.ResolvePermissionAsync("agent-1", "bash");

        // The Ask rule matches in Phase 2 before the Allow rule in Phase 3
        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        decision.MatchedRule!.IsBypassImmune.Should().BeTrue();
    }
}
