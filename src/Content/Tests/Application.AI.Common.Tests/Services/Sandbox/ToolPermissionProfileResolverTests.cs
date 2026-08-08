using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Sandbox;

public sealed class ToolPermissionProfileResolverTests
{
    private SandboxConfig _config = new();
    private readonly ToolPermissionProfileResolver _resolver;

    public ToolPermissionProfileResolverTests()
    {
        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(() => _config);
        _resolver = new ToolPermissionProfileResolver(configMock.Object);
    }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.FileWrite)]
    private sealed class FileToolType { }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess)]
    private sealed class FullToolType { }

    [ToolCapability(ToolCapability.FileRead, MinimumIsolation = SandboxIsolationLevel.Container)]
    private sealed class ContainerToolType { }

    [Fact]
    public void Resolve_NoAttribute_NoOverride_ReturnsDefaultProfile()
    {
        var profile = _resolver.Resolve("unknown_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.AllowedPaths.Should().BeEmpty();
        profile.DeniedPaths.Should().BeEmpty();
        profile.AllowedHosts.Should().BeEmpty();
        profile.DeniedHosts.Should().BeEmpty();
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.None);
    }

    [Fact]
    public void Resolve_AttributeOnly_ReturnsAttributeValues()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));

        var profile = _resolver.Resolve("file_system");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public void Resolve_OverrideOnly_MergesWithDefaults()
    {
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["custom_tool"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./data"],
                    DeniedPaths = ["./data/secrets"],
                    MinimumIsolation = "Process"
                }
            }
        };

        var profile = _resolver.Resolve("custom_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.AllowedPaths.Should().Contain("./data");
        profile.DeniedPaths.Should().Contain("./data/secrets");
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public void Resolve_OverrideDeniedCapabilities_RemovesFromAttribute()
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

        var profile = _resolver.Resolve("full_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.RequiredCapabilities.Should().NotHaveFlag(ToolCapability.NetworkAccess);
    }

    [Fact]
    public void Resolve_OverrideMinimumIsolation_ElevatesButNeverDowngrades()
    {
        _resolver.RegisterToolType("container_tool", typeof(ContainerToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["container_tool"] = new ToolOverrideConfig
                {
                    MinimumIsolation = "Process"
                }
            }
        };

        var profile = _resolver.Resolve("container_tool");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public void Resolve_OverridePaths_MergesLists()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./workspace", "./temp"],
                    DeniedPaths = ["./workspace/.secrets"],
                    AllowedHosts = ["api.example.com"],
                    DeniedHosts = ["evil.example.com"]
                }
            }
        };

        var profile = _resolver.Resolve("file_system");

        profile.AllowedPaths.Should().Contain("./workspace").And.Contain("./temp");
        profile.DeniedPaths.Should().Contain("./workspace/.secrets");
        profile.AllowedHosts.Should().Contain("api.example.com");
        profile.DeniedHosts.Should().Contain("evil.example.com");
    }

    [Fact]
    public void ParseCapabilities_ValidNames_ReturnsFlags()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", "NetworkAccess"]);

        caps.Should().Be(ToolCapability.FileRead | ToolCapability.NetworkAccess);
    }

    [Fact]
    public void ParseCapabilities_InvalidNames_IgnoresGracefully()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", "Bogus", "Subprocess"]);

        caps.Should().Be(ToolCapability.FileRead | ToolCapability.Subprocess);
    }

    [Fact]
    public void ParseCapabilities_Empty_ReturnsNone()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities([]);

        caps.Should().Be(ToolCapability.None);
    }

    [Theory]
    [InlineData("255")]                     // every bit, including undefined ones
    [InlineData(" 255")]                    // and behind a stray space
    [InlineData("4")]                       // the numeric form of NetworkAccess
    [InlineData("FileRead,Subprocess")]     // a combination smuggled into one entry
    public void ParseCapabilities_NonNameEntry_IsIgnored(string entry)
    {
        // #300. ToolCapability is a [Flags] enum, so a permissive parse is worse here than
        // elsewhere: Enum.TryParse accepts "255" and sets every bit at once. On the granting side
        // (SandboxConfig.DefaultGrantedCapabilities, read by ToolInvocationGovernor) that hands a
        // tool every capability the sandbox model defines and makes the capability check unfailable.
        // Combinations remain expressible as separate list entries, which is the shape the config
        // and every test above already use.
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", entry]);

        caps.Should().Be(ToolCapability.FileRead);
    }

    [Fact]
    public void ParseCapabilities_NumericEntry_WouldOtherwiseGrantEveryCapability()
    {
        // Proof the guard is load-bearing rather than decorative: the framework call this replaces
        // accepts "255" and produces a value carrying every defined capability.
        Enum.TryParse<ToolCapability>("255", ignoreCase: true, out var viaFramework).Should().BeTrue();
        viaFramework.Should().HaveFlag(ToolCapability.Subprocess);
        viaFramework.Should().HaveFlag(ToolCapability.NetworkAccess);

        ToolPermissionProfileResolver.ParseCapabilities(["255"]).Should().Be(ToolCapability.None);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("2")]                       // the numeric form of a real isolation level
    [InlineData("None,Container")]
    public void Resolve_NonNameMinimumIsolation_IsIgnoredAndTheAttributeFloorStands(string configured)
    {
        // The override may only elevate isolation, so an unparseable value must land on None and
        // leave the tool's compile-time floor untouched — not on an isolation level that is not a
        // member, which Math.Max would then treat as higher than Container.
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig { MinimumIsolation = configured }
            }
        };

        var profile = _resolver.Resolve("file_system");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }
}
