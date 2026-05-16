using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

public sealed class CapabilityEnforcementTests
{
    private SandboxConfig _config = new();
    private readonly ToolPermissionProfileResolver _resolver;
    private readonly CapabilityEnforcer _enforcer;

    public CapabilityEnforcementTests()
    {
        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(() => _config);
        _resolver = new ToolPermissionProfileResolver(configMock.Object);
        _enforcer = new CapabilityEnforcer(
            _resolver,
            Mock.Of<ILogger<CapabilityEnforcer>>());
    }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.FileWrite)]
    private sealed class FileToolType { }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.NetworkAccess)]
    private sealed class NetworkFileToolType { }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess)]
    private sealed class FullToolType { }

    [ToolCapability(ToolCapability.FileRead)]
    private sealed class ReadOnlyToolType { }

    [ToolCapability(ToolCapability.FileRead, MinimumIsolation = SandboxIsolationLevel.None)]
    private sealed class MinimalIsolationToolType { }

    // --- Capability Checks ---

    [Fact]
    public async Task AllCapabilitiesGranted_PassesThrough()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MissingCapability_ReturnsFail()
    {
        _resolver.RegisterToolType("network_file", typeof(NetworkFileToolType));

        var result = await _enforcer.EnforceAsync(
            "network_file",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("NetworkAccess"));
    }

    [Fact]
    public async Task DeniedPath_ReturnsFail()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
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

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./workspace/secrets/key.pem"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_ReturnsFail()
    {
        _resolver.RegisterToolType("web_fetch", typeof(NetworkFileToolType));
        _config = new SandboxConfig
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

        var result = await _enforcer.EnforceAsync(
            "web_fetch",
            ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["admin.example.com"]);

        result.IsSuccess.Should().BeFalse();
    }

    // --- appsettings Override Behavior ---

    [Fact]
    public async Task AppsettingsOverride_RestrictsAttributeDefaults()
    {
        _resolver.RegisterToolType("full_tool", typeof(FullToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig
                {
                    DeniedCapabilities = ["NetworkAccess"]
                }
            }
        };

        var profile = await _enforcer.ResolveProfileAsync("full_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite);
    }

    [Fact]
    public async Task AppsettingsOverride_CannotExpandBeyondAttribute()
    {
        _resolver.RegisterToolType("read_tool", typeof(ReadOnlyToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["read_tool"] = new ToolOverrideConfig()
            }
        };

        var profile = await _enforcer.ResolveProfileAsync("read_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead);
    }

    // --- Adversarial / Edge Cases ---

    [Fact]
    public async Task PathTraversal_DeniedEvenWhenPrefixMatches()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./workspace"]
                }
            }
        };

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./workspace/../../../etc/passwd"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task MixedSeparators_NormalizedCorrectly()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./workspace"]
                }
            }
        };

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [".\\workspace\\file.txt"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HostWithPort_MatchesDeniedHost()
    {
        _resolver.RegisterToolType("web_fetch", typeof(NetworkFileToolType));
        _config = new SandboxConfig
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

        var result = await _enforcer.EnforceAsync(
            "web_fetch",
            ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["admin.example.com:8080"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyRequestedPaths_PassesThrough()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
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

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: []);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnregisteredTool_NoCapabilitiesRequired_PassesThrough()
    {
        var result = await _enforcer.EnforceAsync(
            "unknown_tool",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeTrue();
    }

    // --- Profile Resolution ---

    [Fact]
    public async Task Resolution_AttributeFallbackWhenNoOverride()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));

        var profile = await _enforcer.ResolveProfileAsync("file_system", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public async Task Resolution_OverrideTakesPrecedence()
    {
        _resolver.RegisterToolType("minimal_tool", typeof(MinimalIsolationToolType));
        _config = new SandboxConfig
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

        var profile = await _enforcer.ResolveProfileAsync("minimal_tool", CancellationToken.None);

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
        profile.DeniedPaths.Should().Contain("./secret");
    }
}
