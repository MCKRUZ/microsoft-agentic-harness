using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Services.Tools;
using Application.Core.Permissions;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

/// <summary>
/// End-to-end tests proving <see cref="PluginPermissionRuleProvider"/>'s <c>StateVersion</c>-keyed
/// cache (#611/#612) actually flips through a REAL <see cref="PluginRegistry"/> — not just a mocked
/// <see cref="IPluginRegistry"/>. Every other test for this provider mocks the registry entirely, so
/// none of them proves the wiring seam a real boundary-status transition depends on: that
/// <see cref="PluginRegistry.StateVersion"/> genuinely advances on <see cref="IPluginRegistry.MarkBoundaryFaulted"/>
/// and that the provider's cache genuinely treats that as invalidation, not just that each half works
/// in isolation. This repo's own history (see CLAUDE.md's "Wiring a Dead Control Ships a Subsystem"
/// note) is controls that tested green while inert precisely because a mock stood in for the real
/// wiring at the one seam that mattered.
/// </summary>
public sealed class PluginPermissionRuleProviderRealRegistryTests
{
    private static LoadedPlugin MakePlugin(string name) =>
        new(name, "1.0", $"/plugins/{name}", new PluginManifest { Name = name },
            PluginLoadStatus.Loaded, SkillPaths: [], McpServerNames: [],
            new PluginDeclaration { Name = name });

    private static PluginPermissionRuleProvider CreateSut(PluginRegistry registry, params string[] firstPartyKeys)
    {
        var skillRegistry = new Mock<ISkillMetadataRegistry>();
        skillRegistry.Setup(r => r.GetAll()).Returns(new List<SkillDefinition>());

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var firstPartyToolLookup = new FirstPartyToolLookup(serviceProvider, new HashSet<string>(firstPartyKeys));

        return new PluginPermissionRuleProvider(
            registry,
            skillRegistry.Object,
            serviceProvider,
            firstPartyToolLookup,
            NullLogger<PluginPermissionRuleProvider>.Instance);
    }

    [Fact]
    public async Task GetRulesAsync_RealRegistryTransitionsVerifiedToFaulted_RulesFlipFromPermissiveToDenyAll()
    {
        var registry = new PluginRegistry();
        registry.Register(MakePlugin("azure"));
        registry.MarkBoundaryVerified("azure");

        var provider = CreateSut(registry, "file_system", "shell");

        var beforeFault = await provider.GetRulesAsync("any-agent");
        beforeFault.Should().BeEmpty("the plugin's boundary is Verified, so no fail-closed rules apply yet");

        registry.MarkBoundaryFaulted("azure", "DeniedTools entry matches no known tool");

        var afterFault = await provider.GetRulesAsync("any-agent");
        afterFault.Should().Contain(r => r.ToolPattern == "file_system"
            && r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
        afterFault.Should().Contain(r => r.ToolPattern == "shell"
            && r.Behavior == PermissionBehaviorType.Deny && r.IsBypassImmune);
    }

    [Fact]
    public async Task GetRulesAsync_RealRegistryNoTransition_ReturnsTheSameCachedInstance()
    {
        // Confirms the cache is actually live through the real registry (not merely "correct by
        // coincidence" because ComputeRules is cheap enough that recomputing every time would also
        // pass the functional assertions above) — a second call with no intervening mutation must
        // return the exact same cached list, not a freshly recomputed equal one.
        var registry = new PluginRegistry();
        registry.Register(MakePlugin("azure"));
        registry.MarkBoundaryFaulted("azure", "reason");

        var provider = CreateSut(registry, "file_system");

        var first = await provider.GetRulesAsync("any-agent");
        var second = await provider.GetRulesAsync("any-agent");

        ReferenceEquals(first, second).Should().BeTrue();
    }
}
