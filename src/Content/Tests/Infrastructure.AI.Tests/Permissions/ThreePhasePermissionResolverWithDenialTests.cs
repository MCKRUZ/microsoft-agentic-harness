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

public sealed class ThreePhasePermissionResolverWithDenialTests
{
    private const int Threshold = 3;
    private const string AgentId = "agent-1";

    private readonly Mock<ISafetyGateRegistry> _safetyGateRegistry = new();
    private readonly GlobPatternMatcher _patternMatcher = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly IOptionsMonitor<AppConfig> _options;

    public ThreePhasePermissionResolverWithDenialTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = new PermissionsConfig
                {
                    DenialRateLimitThreshold = Threshold
                }
            }
        };

        var optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMock.Setup(o => o.CurrentValue).Returns(appConfig);
        _options = optionsMock.Object;

        _safetyGateRegistry
            .Setup(r => r.CheckSafetyGate(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((SafetyGate?)null);
    }

    private ThreePhasePermissionResolver CreateResolver(params ToolPermissionRule[] rules)
    {
        var provider = new Mock<IPermissionRuleProvider>();
        provider.Setup(p => p.GetRulesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

        return new ThreePhasePermissionResolver(
            [provider.Object],
            _safetyGateRegistry.Object,
            _patternMatcher,
            _denialTracker.Object,
            _options,
            Mock.Of<ILogger<ThreePhasePermissionResolver>>());
    }

    [Fact]
    public async Task RateLimitedTool_AutoDenied_BeforeRuleEvaluation()
    {
        _denialTracker.Setup(d => d.IsRateLimited(AgentId, "bash", null)).Returns(true);

        var allowRule = new ToolPermissionRule(
            "bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1);
        var resolver = CreateResolver(allowRule);

        var decision = await resolver.ResolvePermissionAsync(AgentId, "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        decision.Reason.Should().Contain("rate limiter");
        decision.Reason.Should().Contain(Threshold.ToString());
        decision.MatchedRule.Should().BeNull();
    }

    [Fact]
    public async Task NonRateLimitedTool_ProceedsToRuleEvaluation()
    {
        _denialTracker.Setup(d => d.IsRateLimited(AgentId, "bash", null)).Returns(false);

        var allowRule = new ToolPermissionRule(
            "bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1);
        var resolver = CreateResolver(allowRule);

        var decision = await resolver.ResolvePermissionAsync(AgentId, "bash");

        decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        decision.MatchedRule.Should().NotBeNull();
    }
}
