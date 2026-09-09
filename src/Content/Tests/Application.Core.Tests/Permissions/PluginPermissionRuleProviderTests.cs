using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Application.Core.Permissions;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Permissions;

public sealed class PluginPermissionRuleProviderTests : IDisposable
{
    private readonly Mock<IPluginRegistry> _registryMock = new();
    private readonly Mock<ISkillMetadataRegistry> _skillRegistryMock = new();
    private readonly ServiceCollection _services = new();
    private ServiceProvider? _serviceProvider;

    public PluginPermissionRuleProviderTests()
    {
        // Default: no skills discovered. Tests that need plugin-declared tools override this.
        _skillRegistryMock.Setup(r => r.GetAll()).Returns(new List<SkillDefinition>());
    }

    public void Dispose() => _serviceProvider?.Dispose();

    /// <summary>Registers <paramref name="toolName"/> as a global keyed-DI tool (i.e. NOT plugin-owned).</summary>
    private void GivenGlobalKeyedTool(string toolName) =>
        _services.AddKeyedSingleton<ITool>(toolName, (_, _) => Mock.Of<ITool>());

    /// <summary>
    /// Registers a first-party tool under <paramref name="key"/> whose self-reported
    /// <see cref="ITool.Name"/> disagrees with that key — the real, supported shape
    /// <c>ToolCatalogTests.Catalog_ToolWhoseNameDisagreesWithItsKey_...</c> proves exists.
    /// </summary>
    private void GivenKeyedToolWithDivergentName(string key, string publishedName)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(publishedName);
        _services.AddKeyedSingleton(key, mock.Object);
    }

    /// <summary>Registers a keyed-DI tool factory that throws when constructed.</summary>
    private void GivenUnbuildableKeyedTool(string key) =>
        _services.AddKeyedSingleton<ITool>(key, (_, _) =>
            throw new InvalidOperationException("dependency not registered in this host"));

    private PluginPermissionRuleProvider CreateProvider(params string[] knownFirstPartyToolNames)
    {
        _serviceProvider = _services.BuildServiceProvider();
        // #524 round-2: an empty key set is fine for every pre-existing test here — none configures
        // GetBoundaryStatus to return anything but the Moq default (PluginBoundaryStatus.Verified,
        // the enum's zero value), so the blanket-deny path this lookup feeds never fires for them.
        var firstPartyToolLookup = new FirstPartyToolLookup(
            _serviceProvider, new HashSet<string>(knownFirstPartyToolNames));
        return new PluginPermissionRuleProvider(
            _registryMock.Object,
            _skillRegistryMock.Object,
            _serviceProvider,
            firstPartyToolLookup,
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

    private static LoadedPlugin WithStatus(PluginDeclaration declaration, PluginLoadStatus status) =>
        new(declaration.Name, "1.0", $"/plugins/{declaration.Name}", new PluginManifest(),
            status, [], [], declaration);

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

    [Theory]
    [InlineData("2")]                       // the numeric form of Autonomous
    [InlineData(" 2")]                      // and behind a stray space
    [InlineData("99")]                      // outside the defined range
    [InlineData("Restricted,Autonomous")]   // comma-composite, OR'd to Autonomous
    public async Task GetRulesAsync_NonNamePluginAutonomyLevel_EmitsNoBaselineRule(string autonomyLevel)
    {
        // #300. The declaration is authored in a plugin manifest, outside this repo, and the tier it
        // names is converted straight into a permission behaviour — where Autonomous means Allow. A
        // bare Enum.TryParse accepts every value here and would grant a manifest full autonomy off a
        // number. Skipping the rule is the documented behaviour for an invalid level; it only
        // applies if the parse can reject.
        var declaration = new PluginDeclaration { Name = "untrusted", AutonomyLevel = autonomyLevel };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("untrusted", "run_x");

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
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
    public async Task GetRulesAsync_AutonomousPlugin_CannotLoosenGlobalToolItDoesNotOwn()
    {
        // Security (F2): a plugin's SKILL.md names a powerful GLOBAL tool ("bash") alongside its own
        // tool ("run_x"). Marking the plugin Autonomous must NOT emit an authoritative Allow baseline
        // for "bash" (which would auto-approve it agent-wide for every caller) — only the plugin's own
        // "run_x" gets the baseline. The plugin can still use "bash"; it just cannot auto-approve it.
        var declaration = new PluginDeclaration { Name = "trusted", AutonomyLevel = "Autonomous" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("trusted", "run_x", "bash");
        GivenGlobalKeyedTool("bash"); // bash is a shared harness tool, not owned by the plugin

        var rules = await CreateProvider().GetRulesAsync("any-agent");

        rules.Should().Contain(r => r.ToolPattern == "run_x"
            && r.Behavior == PermissionBehaviorType.Allow && r.IsAuthoritativeBaseline);
        rules.Should().NotContain(r => r.ToolPattern == "bash",
            "a global keyed-DI tool the plugin does not own must be excluded from its autonomy baseline");
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

    // --- #524 round-2 code-review: unverified boundary must not leave a global tool unprotected ---

    [Theory]
    [InlineData(PluginBoundaryStatus.Pending)]
    [InlineData(PluginBoundaryStatus.Faulted)]
    public async Task GetRulesAsync_PluginBoundaryUnverified_EmitsBypassImmuneDenyForEveryFirstPartyTool(
        PluginBoundaryStatus status)
    {
        // This is the SECOND enforcement path #524's original fix never touched: ToolChainBuilder
        // only filters the tool SET sourced from the faulted plugin's own skill, but DeniedTools
        // exists specifically to let a plugin block a GLOBAL tool it does not own — reachable through
        // any OTHER skill in the agent, regardless of this plugin's own boundary state. An unresolved
        // entry gives no way to know which specific global tool it was meant to protect, so the
        // fail-closed response is broad: deny every known first-party tool, not scoped to this plugin.
        var declaration = new PluginDeclaration { Name = "azure", DeniedTools = ["file_wrte"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        _registryMock.Setup(r => r.GetBoundaryStatus("azure")).Returns(status);

        var rules = await CreateProvider("file_system", "shell").GetRulesAsync("any-agent");

        rules.Should().Contain(r => r.ToolPattern == "file_system"
            && r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
        rules.Should().Contain(r => r.ToolPattern == "shell"
            && r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
    }

    [Fact]
    public async Task GetRulesAsync_PluginBoundaryVerified_DoesNotEmitBlanketDeny()
    {
        var declaration = new PluginDeclaration { Name = "azure", DeniedTools = ["file_write"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        _registryMock.Setup(r => r.GetBoundaryStatus("azure")).Returns(PluginBoundaryStatus.Verified);

        var rules = await CreateProvider("file_system", "shell").GetRulesAsync("any-agent");

        rules.Should().NotContain(r => r.ToolPattern == "file_system");
        rules.Should().NotContain(r => r.ToolPattern == "shell");
    }

    [Fact]
    public async Task GetRulesAsync_OnlyOneOfMultiplePluginsUnverified_StillEmitsBlanketDenyAgentWide()
    {
        // The blanket deny is not scoped to the specific unverified plugin — it can't be, since which
        // global tool a corrupted entry was meant to protect is unknowable — so even a fully healthy
        // second plugin doesn't limit its reach.
        var healthy = new PluginDeclaration { Name = "healthy" };
        var broken = new PluginDeclaration { Name = "broken", DeniedTools = ["file_wrte"] };
        _registryMock.Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin> { Loaded(healthy), Loaded(broken) });
        _registryMock.Setup(r => r.GetBoundaryStatus("healthy")).Returns(PluginBoundaryStatus.Verified);
        _registryMock.Setup(r => r.GetBoundaryStatus("broken")).Returns(PluginBoundaryStatus.Faulted);

        var rules = await CreateProvider("file_system").GetRulesAsync("any-agent");

        rules.Should().Contain(r => r.ToolPattern == "file_system" && r.Behavior == PermissionBehaviorType.Deny);
    }

    [Theory]
    [InlineData(PluginLoadStatus.Disabled)]
    [InlineData(PluginLoadStatus.Failed)]
    public async Task GetRulesAsync_ANonLoadedPluginRegistered_DoesNotEmitBlanketDeny(PluginLoadStatus status)
    {
        // #613 correctness-review finding: PluginToolBoundaryTracker.Seed is fed only
        // Status == Loaded plugins (PluginToolBoundaryStartupValidator.StartAsync filters before
        // calling it) — a Disabled or Failed plugin is never seeded, so it never gets an explicit
        // MarkBoundaryVerified/Pending/Faulted entry. GetBoundaryStatus.cs's default for an absent
        // plugin is now Pending (#613's fix for the real startup race), which is correct for a Loaded
        // plugin Seed hasn't reached YET — but a Disabled/Failed plugin will NEVER be reached by Seed,
        // contributes zero tools, and has no boundary to distrust. Before this guard, registering any
        // disabled/failed plugin — a completely normal operational state — silently denied every
        // first-party tool for the whole agent, for the process lifetime.
        var healthy = new PluginDeclaration { Name = "healthy" };
        var notLoaded = new PluginDeclaration { Name = "not-loaded" };
        _registryMock.Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin> { Loaded(healthy), WithStatus(notLoaded, status) });
        _registryMock.Setup(r => r.GetBoundaryStatus("healthy")).Returns(PluginBoundaryStatus.Verified);
        _registryMock.Setup(r => r.GetBoundaryStatus("not-loaded")).Returns(PluginBoundaryStatus.Pending);

        var rules = await CreateProvider("file_system", "shell").GetRulesAsync("any-agent");

        rules.Should().NotContain(r => r.ToolPattern == "file_system");
        rules.Should().NotContain(r => r.ToolPattern == "shell");
    }

    // --- #612: rule.ToolPattern is matched against a tool's PUBLISHED (self-reported) name at
    // invocation (ThreePhasePermissionResolver.Matches), but a plugin declares DeniedTools against
    // the DI registration key (the identifier ApplyPluginToolBoundary's existence check uses) — and
    // a keyed ITool can legitimately report a Name that disagrees with its own key
    // (ToolCatalogTests.Catalog_ToolWhoseNameDisagreesWithItsKey_...). Without also emitting a rule
    // against the resolved published name, the deny silently never fires for such a tool.

    [Fact]
    public async Task GetRulesAsync_DeniedToolNameDisagreesWithItsKey_AlsoEmitsDenyForThePublishedName()
    {
        var declaration = new PluginDeclaration { Name = "limited", DeniedTools = ["registered_key"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenKeyedToolWithDivergentName("registered_key", "self_reported_name");

        var rules = await CreateProvider("registered_key").GetRulesAsync("any-agent");

        rules.Should().Contain(r => r.ToolPattern == "registered_key" && r.Behavior == PermissionBehaviorType.Deny);
        rules.Should().Contain(r => r.ToolPattern == "self_reported_name"
            && r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
    }

    [Fact]
    public async Task GetRulesAsync_DeniedToolNameMatchesItsKey_EmitsOnlyOneDenyRule()
    {
        // No divergence: must not double-emit a redundant rule for the common case.
        var declaration = new PluginDeclaration { Name = "limited", DeniedTools = ["bash"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenKeyedToolWithDivergentName("bash", "bash");

        var rules = await CreateProvider("bash").GetRulesAsync("any-agent");

        rules.Should().ContainSingle(r => r.ToolPattern == "bash");
    }

    [Fact]
    public async Task GetRulesAsync_DeniedToolUnbuildable_StillEmitsKeyOnlyDenyWithoutThrowing()
    {
        // A first-party tool whose constructor needs a dependency this host never wired (the exact
        // failure mode that broke boot the first time full-registry construction was tried for #524's
        // existence check) must not crash GetRulesAsync. The key-pattern deny rule must still be
        // emitted for it.
        var declaration = new PluginDeclaration { Name = "limited", DeniedTools = ["unbuildable"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenUnbuildableKeyedTool("unbuildable");
        var provider = CreateProvider("unbuildable");

        var act = async () => await provider.GetRulesAsync("any-agent");
        await act.Should().NotThrowAsync();

        var rules = await provider.GetRulesAsync("any-agent");
        rules.Should().Contain(r => r.ToolPattern == "unbuildable" && r.Behavior == PermissionBehaviorType.Deny);
    }

    [Fact]
    public async Task GetRulesAsync_UnverifiedBoundary_BlanketDenyStaysKeyOnly_DoesNotResolveEveryToolsName()
    {
        // #612 code-review: the blanket unverified-boundary deny deliberately does NOT resolve
        // published names for the whole first-party registry — doing so would construct every
        // registered tool as a side effect of a permission check whenever any plugin's boundary is
        // merely unverified, not because anything invoked those tools (narrowed after review; see
        // AddDenyRuleWithPublishedNameCoverage's remarks). Only the per-plugin DeniedTools loop
        // (above) resolves published names, for its small, explicitly-authored list. This is a pin
        // for the accepted trade-off, not a gap this PR still owns.
        var declaration = new PluginDeclaration { Name = "azure", DeniedTools = ["file_wrte"] };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        _registryMock.Setup(r => r.GetBoundaryStatus("azure")).Returns(PluginBoundaryStatus.Faulted);
        GivenKeyedToolWithDivergentName("registered_key", "self_reported_name");

        var rules = await CreateProvider("registered_key").GetRulesAsync("any-agent");

        rules.Should().Contain(r => r.ToolPattern == "registered_key" && r.Behavior == PermissionBehaviorType.Deny);
        rules.Should().NotContain(r => r.ToolPattern == "self_reported_name");
    }

    // --- #611: GetRulesAsync is called fresh on every tool-permission resolution. Once name
    // resolution (above) means it may construct first-party tools, recomputing unconditionally
    // turns an unverified-boundary state into unbounded repeated construction cost. Cache the
    // result, keyed on IPluginRegistry.StateVersion, and recompute only when it changes.

    [Fact]
    public async Task GetRulesAsync_CalledTwiceWithNoRegistryChange_DoesNotRecompute()
    {
        var declaration = new PluginDeclaration { Name = "p", AutonomyLevel = "Autonomous" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        _registryMock.Setup(r => r.StateVersion).Returns(1);
        GivenPluginSkillDeclaresTools("p", "run_x");
        var provider = CreateProvider();

        await provider.GetRulesAsync("any-agent");
        await provider.GetRulesAsync("any-agent");

        _registryMock.Verify(r => r.GetLoadedPlugins(), Times.Once);
    }

    [Fact]
    public async Task GetRulesAsync_StateVersionChangesBetweenCalls_Recomputes()
    {
        var declaration = new PluginDeclaration { Name = "p", AutonomyLevel = "Autonomous" };
        _registryMock.Setup(r => r.GetLoadedPlugins()).Returns(new List<LoadedPlugin> { Loaded(declaration) });
        GivenPluginSkillDeclaresTools("p", "run_x");
        var provider = CreateProvider();
        _registryMock.SetupSequence(r => r.StateVersion).Returns(1).Returns(2);

        await provider.GetRulesAsync("any-agent");
        await provider.GetRulesAsync("any-agent");

        _registryMock.Verify(r => r.GetLoadedPlugins(), Times.Exactly(2));
    }
}
