using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

/// <summary>
/// Tests for <see cref="CapabilityEnforcer"/>/<see cref="ToolPermissionProfileResolver"/>. A tool's
/// base declaration comes from a registered <see cref="ITool"/>'s own
/// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/> via keyed DI, not
/// the dead <c>[ToolCapabilityAttribute]</c>/<c>RegisterToolType</c> mechanism this replaces (#387).
/// </summary>
public sealed class CapabilityEnforcementTests
{
    private static ITool FileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite));

    private static ITool NetworkFileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.NetworkAccess));

    private static ITool FullTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess));

    private static ITool ReadOnlyTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead);

    // The old [ToolCapability] attribute defaulted MinimumIsolation to Process when a tool declared
    // one without setting it explicitly; ITool.MinimumIsolation defaults to None instead (matching
    // every production tool, none of which ever carried the dead attribute). This fake preserves the
    // "a declared floor is honoured" scenario by declaring Process explicitly.
    private static ITool ProcessIsolationTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite)
        && t.MinimumIsolation == SandboxIsolationLevel.Process);

    private static ITool MinimalIsolationTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead
        && t.MinimumIsolation == SandboxIsolationLevel.None);

    private static (ToolPermissionProfileResolver Resolver, CapabilityEnforcer Enforcer) Build(
        SandboxConfig? config = null,
        params (string Name, ITool Tool)[] tools)
    {
        var services = new ServiceCollection();
        foreach (var (name, tool) in tools)
            services.AddKeyedSingleton<ITool>(name, (_, _) => tool);

        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(config ?? new SandboxConfig());

        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string>(tools.Select(t => t.Name)));
        var resolver = new ToolPermissionProfileResolver(lookup, configMock.Object);
        var enforcer = new CapabilityEnforcer(resolver, Mock.Of<ILogger<CapabilityEnforcer>>());
        return (resolver, enforcer);
    }

    // --- Capability Checks ---

    [Fact]
    public async Task AllCapabilitiesGranted_PassesThrough()
    {
        var (_, enforcer) = Build(tools: ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MissingCapability_ReturnsFail()
    {
        var (_, enforcer) = Build(tools: ("network_file", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "network_file",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("NetworkAccess"));
    }

    [Fact]
    public async Task DeniedCapability_ThatToolRequires_FailsEnforcement()
    {
        // The core #405 behavior change: a tool whose requirement intersects its own per-tool deny
        // is refused outright by EnforceAsync, even when the caller granted every capability the
        // tool's undiminished declaration lists.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var (_, enforcer) = Build(config, ("full_tool", FullTool()));

        var result = await enforcer.EnforceAsync(
            "full_tool",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);

        result.IsSuccess.Should().BeFalse(
            "a per-tool deny must refuse the call, not silently shrink the requirement");
    }

    // --- appsettings Override Behavior ---

    [Fact]
    public async Task AppsettingsOverride_KeepsDeclarationUndiminished_NarrowsOnlyEffective()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var (_, enforcer) = Build(config, ("full_tool", FullTool()));

        var profile = await enforcer.ResolveProfileAsync("full_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess,
            "the tool's own declaration must never be reduced by a deny override");
        profile.EffectiveCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
    }

    [Fact]
    public async Task AppsettingsOverride_CannotExpandBeyondDeclaration()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["read_tool"] = new ToolOverrideConfig()
            }
        };
        var (_, enforcer) = Build(config, ("read_tool", ReadOnlyTool()));

        var profile = await enforcer.ResolveProfileAsync("read_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead);
    }

    // --- Adversarial / Edge Cases ---

    [Fact]
    public async Task UnregisteredTool_NoCapabilitiesRequired_PassesThrough()
    {
        var (_, enforcer) = Build();

        var result = await enforcer.EnforceAsync(
            "unknown_tool",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeTrue();
    }

    // --- Profile Resolution ---

    [Fact]
    public async Task Resolution_DeclarationFallbackWhenNoOverride()
    {
        var (_, enforcer) = Build(tools: ("file_system", ProcessIsolationTool()));

        var profile = await enforcer.ResolveProfileAsync("file_system", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public async Task Resolution_OverrideTakesPrecedence()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["minimal_tool"] = new ToolOverrideConfig
                {
                    MinimumIsolation = "Process",
                    DeniedCapabilities = ["FileRead"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("minimal_tool", MinimalIsolationTool()));

        var profile = await enforcer.ResolveProfileAsync("minimal_tool", CancellationToken.None);

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
        profile.DeniedCapabilities.Should().Be(ToolCapability.FileRead);
    }
}
