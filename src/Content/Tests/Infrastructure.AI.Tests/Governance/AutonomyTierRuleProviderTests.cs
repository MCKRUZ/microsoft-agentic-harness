using Application.AI.Common.Interfaces.Governance;
using Application.Core.Permissions;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

public sealed class AutonomyTierRuleProviderTests
{
    private readonly Mock<IAutonomyTierResolver> _resolverMock = new();
    private readonly Mock<ILogger<AutonomyTierRuleProvider>> _loggerMock = new();

    private AutonomyTierRuleProvider CreateProvider(PermissionsConfig? permissions = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = permissions ?? new PermissionsConfig()
            }
        };

        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
            o => o.CurrentValue == appConfig);

        return new AutonomyTierRuleProvider(
            _resolverMock.Object,
            optionsMonitor,
            _loggerMock.Object);
    }

    [Fact]
    public void Source_ReturnsAutonomyTier()
    {
        // Arrange
        var provider = CreateProvider();

        // Act & Assert
        provider.Source.Should().Be(PermissionRuleSource.AutonomyTier);
    }

    [Fact]
    public async Task GetRulesAsync_RestrictedTier_GeneratesGlobalAskRule()
    {
        // Arrange
        _resolverMock
            .Setup(r => r.Resolve(SubagentType.Explore))
            .Returns(AutonomyLevel.Restricted);

        var permissions = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Restricted"] = new() { DefaultBehavior = "Ask" }
            }
        };

        var provider = CreateProvider(permissions);

        // Act
        var rules = await provider.GetRulesAsync("Explore");

        // Assert
        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Priority.Should().Be(0);
        rule.Source.Should().Be(PermissionRuleSource.AutonomyTier);
    }

    [Fact]
    public async Task GetRulesAsync_AutonomousTier_GeneratesGlobalAllowRule()
    {
        // Arrange
        _resolverMock
            .Setup(r => r.Resolve(SubagentType.Execute))
            .Returns(AutonomyLevel.Autonomous);

        var permissions = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Autonomous"] = new() { DefaultBehavior = "Allow" }
            }
        };

        var provider = CreateProvider(permissions);

        // Act
        var rules = await provider.GetRulesAsync("Execute");

        // Assert
        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Allow);
        rule.Priority.Should().Be(0);
    }

    [Fact]
    public async Task GetRulesAsync_WithToolOverrides_GeneratesOverrideRulesAtHigherPriority()
    {
        // Arrange
        _resolverMock
            .Setup(r => r.Resolve(SubagentType.Explore))
            .Returns(AutonomyLevel.Restricted);

        var permissions = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Restricted"] = new()
                {
                    DefaultBehavior = "Ask",
                    ToolOverrides = new Dictionary<string, string>
                    {
                        ["query_kg"] = "Allow"
                    }
                }
            }
        };

        var provider = CreateProvider(permissions);

        // Act
        var rules = await provider.GetRulesAsync("Explore");

        // Assert
        rules.Should().HaveCount(2);

        var globalRule = rules.First(r => r.ToolPattern == "*");
        globalRule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        globalRule.Priority.Should().Be(0);

        var overrideRule = rules.First(r => r.ToolPattern == "query_kg");
        overrideRule.Behavior.Should().Be(PermissionBehaviorType.Allow);
        overrideRule.Priority.Should().Be(10);
    }

    [Fact]
    public async Task GetRulesAsync_NoTierPolicy_UsesDefaultBehavior()
    {
        // Arrange
        _resolverMock
            .Setup(r => r.Resolve(SubagentType.Explore))
            .Returns(AutonomyLevel.Restricted);

        var permissions = new PermissionsConfig
        {
            DefaultBehavior = "Ask",
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>()
        };

        var provider = CreateProvider(permissions);

        // Act
        var rules = await provider.GetRulesAsync("Explore");

        // Assert
        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Priority.Should().Be(0);
    }

    [Fact]
    public async Task GetRulesAsync_UnparsableAgentId_FallsBackToDefaultLevel()
    {
        // Arrange — "not-a-subagent-type" won't parse as SubagentType,
        // so the provider falls back to config DefaultAutonomyLevel
        var permissions = new PermissionsConfig
        {
            DefaultAutonomyLevel = "Supervised",
            DefaultBehavior = "Ask",
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Supervised"] = new() { DefaultBehavior = "Ask" }
            }
        };

        var provider = CreateProvider(permissions);

        // Act
        var rules = await provider.GetRulesAsync("not-a-subagent-type");

        // Assert
        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
    }
}
