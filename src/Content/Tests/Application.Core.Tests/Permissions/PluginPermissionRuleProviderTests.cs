using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.Core.Permissions;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Permissions;

public sealed class PluginPermissionRuleProviderTests
{
    private readonly Mock<IPluginRegistry> _registryMock = new();
    private readonly Mock<ISkillMetadataRegistry> _skillRegistryMock = new();

    public PluginPermissionRuleProviderTests()
    {
        // Default: no skills discovered. Tests that need plugin-declared tools override this.
        _skillRegistryMock.Setup(r => r.GetAll()).Returns(new List<SkillDefinition>());
    }

    private PluginPermissionRuleProvider CreateProvider()
    {
        return new PluginPermissionRuleProvider(
            _registryMock.Object,
            _skillRegistryMock.Object,
            NullLogger<PluginPermissionRuleProvider>.Instance);
    }

    /// <summary>Attributes one skill declaring <paramref name="toolNames"/> to <paramref name="pluginName"/>.</summary>
    private void GivenPluginSkillDeclaresTools(string pluginName, params string[] toolNames)
    {
        _skillRegistryMock.Setup(r => r.GetAll()).Returns(new List<SkillDefinition>
        {
            new()
            {
                Id = $"{pluginName}-skill",
                PluginSource = pluginName,
                AllowedTools = toolNames.ToList()
            }
        });
    }

    private static LoadedPlugin Loaded(PluginDeclaration declaration) =>
        new(declaration.Name, "1.0", $"/plugins/{declaration.Name}", new PluginManifest(),
            PluginLoadStatus.Loaded, [$"/plugins/{declaration.Name}/skills"], [], declaration);

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
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });

        var rules = await CreateProvider().GetRulesAsync("any-agent");
        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_RestrictedPlugin_EmitsAuthoritativeAskRulesForDeclaredTools()
    {
        var declaration = new PluginDeclaration { Name = "untrusted", AutonomyLevel = "Restricted" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("untrusted", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("run_x");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Source.Should().Be(PermissionRuleSource.PluginDeclaration);
        rule.IsAuthoritativeBaseline.Should().BeTrue();
    }

    [Fact]
    public async Task GetRulesAsync_AutonomousPlugin_EmitsAuthoritativeAllowRulesForDeclaredTools()
    {
        var declaration = new PluginDeclaration { Name = "trusted", AutonomyLevel = "Autonomous" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("trusted", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        rules[0].ToolPattern.Should().Be("run_x");
        rules[0].Behavior.Should().Be(PermissionBehaviorType.Allow);
        rules[0].IsAuthoritativeBaseline.Should().BeTrue();
    }

    [Fact]
    public async Task GetRulesAsync_MultipleDeclaredTools_EmitsOneBaselinePerDistinctToolName()
    {
        var declaration = new PluginDeclaration { Name = "p", AutonomyLevel = "Supervised" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("p", "a", "b", "a"); // duplicate must collapse

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Select(r => r.ToolPattern).Should().BeEquivalentTo("a", "b");
        rules.Should().OnlyContain(r => r.Behavior == PermissionBehaviorType.Ask && r.IsAuthoritativeBaseline);
    }

    [Fact]
    public async Task GetRulesAsync_SupervisedPlugin_EmitsBaselineForRealDeclaredToolName_NotWildcard()
    {
        // Regression pin for the inert-baseline bug: the Supervised baseline must apply to the
        // plugin skill's REAL declared tool name — never the synthetic "{plugin}:*" wildcard that
        // no live tool name ever matches.
        var declaration = new PluginDeclaration { Name = "sentinel", AutonomyLevel = "Supervised" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("sentinel", "deploy_widget");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().Contain(
            r => r.ToolPattern == "deploy_widget"
                 && r.Behavior == PermissionBehaviorType.Ask
                 && r.IsAuthoritativeBaseline,
            "the Supervised baseline must apply to the plugin skill's real declared tool");
        rules.Should().NotContain(r => r.ToolPattern == "sentinel:*",
            "the inert synthetic wildcard must be gone");
    }

    [Fact]
    public async Task GetRulesAsync_AutonomyLevelSet_ButNoDeclaredTools_SkipsBaseline()
    {
        // Injected-mode plugin: skills declare no tools, so the baseline cannot be scoped to real
        // tool names and must be skipped (with a warning) rather than emitting an inert wildcard.
        var declaration = new PluginDeclaration { Name = "injected", AutonomyLevel = "Supervised" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        // No skills attributed to "injected".

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
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
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("limited", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().HaveCount(3); // 1 authoritative baseline + 2 Deny overrides
        rules.Should().Contain(r => r.ToolPattern == "run_x"
            && r.Behavior == PermissionBehaviorType.Ask && r.IsAuthoritativeBaseline);
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
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });

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
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("plugin", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        var baseline = rules.First(r => r.ToolPattern == "run_x");
        var deny = rules.First(r => r.ToolPattern == "dangerous_tool");

        deny.Priority.Should().BeLessThan(baseline.Priority);
        deny.Behavior.Should().Be(PermissionBehaviorType.Deny);
        deny.IsBypassImmune.Should().BeTrue();
    }

    [Fact]
    public async Task GetRulesAsync_InvalidAutonomyLevel_SkipsPlugin()
    {
        var declaration = new PluginDeclaration { Name = "bad", AutonomyLevel = "NotAValidLevel" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("bad", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
    }
}
