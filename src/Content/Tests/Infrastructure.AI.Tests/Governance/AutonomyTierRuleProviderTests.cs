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

    [Theory]
    [InlineData("99")]
    [InlineData(" 99")]
    [InlineData("1")]                       // the numeric form of a real subagent type
    [InlineData("Explore,General")]
    public async Task GetRulesAsync_NonNameAgentId_IsNotTreatedAsASubagentType(string agentId)
    {
        // #300. agentId reaches this provider from the caller, and a bare Enum.TryParse would let
        // "1" address a subagent type positionally — resolving that agent's tier for a caller who
        // never named it. The tier resolver must not be consulted at all for a non-name.
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

        var rules = await provider.GetRulesAsync(agentId);

        rules.Should().ContainSingle();
        rules[0].Behavior.Should().Be(PermissionBehaviorType.Ask);
        _resolverMock.Verify(r => r.Resolve(It.IsAny<SubagentType>()), Times.Never);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]                       // the numeric form of a real behaviour
    [InlineData("Allow,Deny")]
    public async Task GetRulesAsync_NonNameDefaultBehavior_FallsBackToAsk(string behavior)
    {
        // The baseline behaviour for every tool the agent can reach. A permissive parse turns a typo
        // into a behaviour nobody declared — and PermissionBehaviorType.Allow is one value away, so
        // the failure direction is "silently permit", not "silently break".
        _resolverMock
            .Setup(r => r.Resolve(SubagentType.Explore))
            .Returns(AutonomyLevel.Restricted);

        var permissions = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Restricted"] = new() { DefaultBehavior = behavior }
            }
        };

        var provider = CreateProvider(permissions);

        var rules = await provider.GetRulesAsync("Explore");

        rules.Should().ContainSingle();
        rules[0].Behavior.Should().Be(PermissionBehaviorType.Ask);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("Allow,Deny")]
    public async Task GetRulesAsync_NonNameToolOverrideBehavior_SkipsTheOverrideRow(string behavior)
    {
        // A per-tool override that cannot be parsed must be dropped, leaving only the baseline rule.
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
                    ToolOverrides = new Dictionary<string, string> { ["file_system"] = behavior }
                }
            }
        };

        var provider = CreateProvider(permissions);

        var rules = await provider.GetRulesAsync("Explore");

        rules.Should().ContainSingle();
        rules[0].ToolPattern.Should().Be("*");
    }

    [Fact]
    public async Task GetRulesAsync_NamedToolOverrideBehavior_IsStillEmitted()
    {
        // The control for the theory above: refusing non-names must not mean dropping real overrides.
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
                        ["file_system"] = nameof(PermissionBehaviorType.Deny)
                    }
                }
            }
        };

        var provider = CreateProvider(permissions);

        var rules = await provider.GetRulesAsync("Explore");

        rules.Should().HaveCount(2);
        rules.Should().ContainSingle(r => r.ToolPattern == "file_system"
            && r.Behavior == PermissionBehaviorType.Deny);
    }
}
