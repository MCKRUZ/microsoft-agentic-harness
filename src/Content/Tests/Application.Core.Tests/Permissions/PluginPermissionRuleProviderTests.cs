using Application.AI.Common.Interfaces.Plugins;
using Application.Core.Permissions;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Permissions;

public sealed class PluginPermissionRuleProviderTests
{
    private readonly Mock<IPluginRegistry> _registryMock = new();

    private PluginPermissionRuleProvider CreateProvider()
    {
        return new PluginPermissionRuleProvider(
            _registryMock.Object,
            NullLogger<PluginPermissionRuleProvider>.Instance);
    }

    [Fact]
    public void Source_ReturnsPluginDeclaration()
    {
        var provider = CreateProvider();
        provider.Source.Should().Be(PermissionRuleSource.PluginDeclaration);
    }

    [Fact]
    public async Task GetRulesAsync_NoPluginsLoaded_ReturnsEmpty()
    {
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>());
        var rules = await CreateProvider().GetRulesAsync("any-agent");
        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_PluginWithNoAutonomyLevel_ReturnsEmpty()
    {
        var declaration = new PluginDeclaration { Name = "azure", AutonomyLevel = null };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("azure", "1.0", "/plugins/azure", new PluginManifest(),
                PluginLoadStatus.Loaded, ["skill1"], ["azure:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");
        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_RestrictedPlugin_EmitsAskRules()
    {
        var declaration = new PluginDeclaration { Name = "untrusted", AutonomyLevel = "Restricted" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("untrusted", "1.0", "/plugins/untrusted", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["untrusted:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("untrusted:*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Source.Should().Be(PermissionRuleSource.PluginDeclaration);
    }

    [Fact]
    public async Task GetRulesAsync_AutonomousPlugin_EmitsAllowRules()
    {
        var declaration = new PluginDeclaration { Name = "trusted", AutonomyLevel = "Autonomous" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("trusted", "1.0", "/plugins/trusted", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["trusted:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        rules[0].Behavior.Should().Be(PermissionBehaviorType.Allow);
    }

    [Fact]
    public async Task GetRulesAsync_PluginWithDeniedTools_EmitsDenyRules()
    {
        var declaration = new PluginDeclaration
        {
            Name = "limited",
            AutonomyLevel = "Supervised",
            DeniedTools = ["bash", "deploy_production"]
        };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("limited", "1.0", "/plugins/limited", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["limited:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().HaveCount(3); // 1 global Ask + 2 Deny overrides
        rules.Should().Contain(r => r.ToolPattern == "limited:*" && r.Behavior == PermissionBehaviorType.Ask);
        rules.Should().Contain(r => r.ToolPattern == "bash" && r.Behavior == PermissionBehaviorType.Deny);
        rules.Should().Contain(r => r.ToolPattern == "deploy_production" && r.Behavior == PermissionBehaviorType.Deny);
    }

    [Fact]
    public async Task GetRulesAsync_DeniedToolsWithoutAutonomyLevel_StillEmitsBypassImmuneDenyRules()
    {
        // DeniedTools are bypass-immune and must be enforced regardless of whether the
        // plugin also sets an AutonomyLevel. A plugin that only denies tools (no autonomy
        // override) must still contribute its Deny rules.
        var declaration = new PluginDeclaration
        {
            Name = "deny-only",
            AutonomyLevel = null,
            DeniedTools = ["bash", "deploy_production"]
        };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("deny-only", "1.0", "/plugins/deny-only", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["deny-only:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().HaveCount(2, "no baseline autonomy rule, but both Deny rules must be present");
        rules.Should().OnlyContain(r =>
            r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
        rules.Should().Contain(r => r.ToolPattern == "bash");
        rules.Should().Contain(r => r.ToolPattern == "deploy_production");
    }

    [Fact]
    public async Task GetRulesAsync_DenyRules_HaveHigherPriorityThanBaseline()
    {
        var declaration = new PluginDeclaration
        {
            Name = "plugin",
            AutonomyLevel = "Autonomous",
            DeniedTools = ["dangerous_tool"]
        };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("plugin", "1.0", "/plugins/plugin", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["plugin:server"], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        var baseline = rules.First(r => r.ToolPattern == "plugin:*");
        var deny = rules.First(r => r.ToolPattern == "dangerous_tool");

        deny.Priority.Should().BeLessThan(baseline.Priority);
        deny.Behavior.Should().Be(PermissionBehaviorType.Deny);
        deny.IsBypassImmune.Should().BeTrue();
    }

    [Fact]
    public async Task GetRulesAsync_InvalidAutonomyLevel_SkipsPlugin()
    {
        var declaration = new PluginDeclaration { Name = "bad", AutonomyLevel = "NotAValidLevel" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin>
        {
            new("bad", "1.0", "/plugins/bad", new PluginManifest(),
                PluginLoadStatus.Loaded, [], [], declaration)
        });

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
    }
}
