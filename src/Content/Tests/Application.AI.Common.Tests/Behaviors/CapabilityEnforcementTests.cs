using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
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

        var resolver = new ToolPermissionProfileResolver(
            services.BuildServiceProvider(),
            configMock.Object,
            new HashSet<string>(tools.Select(t => t.Name)));
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
    public async Task DeniedPath_ReturnsFail()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./workspace"],
                    DeniedPaths = ["./workspace/secrets"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./workspace/secrets/key.pem"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_ReturnsFail()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["web_fetch"] = new ToolOverrideConfig
                {
                    AllowedHosts = ["*.example.com"],
                    DeniedHosts = ["admin.example.com"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("web_fetch", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "web_fetch",
            ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["admin.example.com"]);

        result.IsSuccess.Should().BeFalse();
    }

    // --- appsettings Override Behavior ---

    [Fact]
    public async Task AppsettingsOverride_RestrictsDeclaredDefaults()
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
            ToolCapability.FileRead | ToolCapability.FileWrite);
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
    public async Task PathTraversal_DeniedEvenWhenPrefixMatches()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["./workspace"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./workspace/../../../etc/passwd"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task MixedSeparators_NormalizedCorrectly()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["./workspace"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [".\\workspace\\file.txt"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HostWithPort_MatchesDeniedHost()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["web_fetch"] = new ToolOverrideConfig
                {
                    AllowedHosts = ["*.example.com"],
                    DeniedHosts = ["admin.example.com"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("web_fetch", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "web_fetch",
            ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["admin.example.com:8080"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyRequestedPaths_PassesThrough()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./workspace"],
                    DeniedPaths = ["./workspace/secrets"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: []);

        result.IsSuccess.Should().BeTrue();
    }

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
                    DeniedPaths = ["./secret"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("minimal_tool", MinimalIsolationTool()));

        var profile = await enforcer.ResolveProfileAsync("minimal_tool", CancellationToken.None);

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
        profile.DeniedPaths.Should().Contain("./secret");
    }
}
