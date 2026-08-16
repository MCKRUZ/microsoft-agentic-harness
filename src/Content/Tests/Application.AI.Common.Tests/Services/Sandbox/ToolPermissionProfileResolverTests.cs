using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Sandbox;

/// <summary>
/// Tests for <see cref="ToolPermissionProfileResolver"/>. The base declaration now comes from a
/// registered <see cref="ITool"/>'s own <see cref="ITool.RequiredCapabilities"/>/
/// <see cref="ITool.MinimumIsolation"/> via bounded-key-set keyed DI, mirroring
/// <c>ToolCapabilityResolverTests</c>'s pattern for the sibling composition-capability resolver —
/// not the dead <c>[ToolCapabilityAttribute]</c>/<c>RegisterToolType</c> mechanism this replaces
/// (#387).
/// </summary>
public sealed class ToolPermissionProfileResolverTests
{
    private static ToolPermissionProfileResolver BuildResolver(
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
        return new ToolPermissionProfileResolver(lookup, configMock.Object);
    }

    private static ITool FileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite));

    private static ITool FullTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess));

    private static ITool ContainerTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead
        && t.MinimumIsolation == SandboxIsolationLevel.Container);

    [Fact]
    public void Resolve_UnregisteredName_NoOverride_ReturnsDefaultProfile()
    {
        var resolver = BuildResolver();

        var profile = resolver.Resolve("unknown_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.AllowedPaths.Should().BeEmpty();
        profile.DeniedPaths.Should().BeEmpty();
        profile.AllowedHosts.Should().BeEmpty();
        profile.DeniedHosts.Should().BeEmpty();
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.None);
    }

    [Fact]
    public void Resolve_DeclarationOnly_ReturnsDeclaredValues()
    {
        var resolver = BuildResolver(tools: ("file_system", FileTool()));

        var profile = resolver.Resolve("file_system");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.None);
    }

    [Fact]
    public void Resolve_OverrideOnly_MergesWithDefaults()
    {
        var config = new SandboxConfig
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
        var resolver = BuildResolver(config);

        var profile = resolver.Resolve("custom_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.AllowedPaths.Should().Contain("./data");
        profile.DeniedPaths.Should().Contain("./data/secrets");
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public void Resolve_OverrideDeniedCapabilities_RemovesFromDeclaration()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.RequiredCapabilities.Should().NotHaveFlag(ToolCapability.NetworkAccess);
    }

    [Fact]
    public void Resolve_OverrideMinimumIsolation_ElevatesButNeverDowngrades()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["container_tool"] = new ToolOverrideConfig { MinimumIsolation = "Process" }
            }
        };
        var resolver = BuildResolver(config, ("container_tool", ContainerTool()));

        var profile = resolver.Resolve("container_tool");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public void Resolve_OverridePaths_MergesLists()
    {
        var config = new SandboxConfig
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
        var resolver = BuildResolver(config, ("file_system", FileTool()));

        var profile = resolver.Resolve("file_system");

        profile.AllowedPaths.Should().Contain("./workspace").And.Contain("./temp");
        profile.DeniedPaths.Should().Contain("./workspace/.secrets");
        profile.AllowedHosts.Should().Contain("api.example.com");
        profile.DeniedHosts.Should().Contain("evil.example.com");
    }

    [Fact]
    public void Resolve_NameNotInBoundedKeySet_IsTreatedAsUnregistered()
    {
        // A name that carries a real ITool registration in the container but was not included in the
        // bounded key set (e.g. an MCP or bundle-owned name) must never be probed — see the resolver's
        // remarks. Simulated here by registering the tool but passing an empty key set.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("mcp_tool", (_, _) => FullTool());
        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(new SandboxConfig());
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string>());
        var resolver = new ToolPermissionProfileResolver(lookup, configMock.Object);

        var profile = resolver.Resolve("mcp_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
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
    [InlineData("Bogus")]
    public void ParseCapabilities_NumericOrUnknownEntry_IsIgnored(string entry)
    {
        // #300. ToolCapability is a [Flags] enum, so a permissive parse is worse here than
        // elsewhere: Enum.TryParse accepts "255" and sets every bit at once. On the granting side
        // (SandboxConfig.DefaultGrantedCapabilities, read by ToolInvocationGovernor) that hands a
        // tool every capability the sandbox model defines and makes the capability check unfailable.
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", entry]);

        caps.Should().Be(ToolCapability.FileRead);
    }

    [Fact]
    public void ParseCapabilities_CommaSeparatedNamesInOneEntry_AreAllHonoured()
    {
        // Deliberately NOT treated as a rejected composite, unlike every other enum in the #300
        // sweep. This method also feeds ToolOverrideConfig.DeniedCapabilities, where dropping an
        // entry fails OPEN — the capability stays granted, and DockerSandboxExecutor reads those
        // same bits for container network access and read-only bind mounts. Refusing a comma entry
        // would silently turn a working deny into a live grant on upgrade. Each token is still
        // validated by name individually, so the numeric form gains nothing.
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["NetworkAccess, FileWrite"]);

        caps.Should().Be(ToolCapability.NetworkAccess | ToolCapability.FileWrite);
    }

    [Fact]
    public void Resolve_CommaSeparatedDeniedCapabilities_StillDeny()
    {
        // The regression this guards, stated where it actually bites: a deny that stops denying.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess,FileWrite"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead);
        profile.RequiredCapabilities.Should().NotHaveFlag(ToolCapability.NetworkAccess);
        profile.RequiredCapabilities.Should().NotHaveFlag(ToolCapability.FileWrite);
    }

    [Fact]
    public void Resolve_NumericDeniedCapability_IsIgnoredAndDoesNotDenyEverything()
    {
        // The other half of the contract: a numeric deny entry is refused rather than expanded to
        // every bit. "255" would otherwise strip all capabilities from the tool.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["255"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);
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
    public void Resolve_NonNameMinimumIsolation_IsIgnoredAndTheDeclaredFloorStands(string configured)
    {
        // The override may only elevate isolation, so an unparseable value must land on None and
        // leave the tool's declared floor untouched — not on an isolation level that is not a
        // member, which Math.Max would then treat as higher than Container.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["container_tool"] = new ToolOverrideConfig { MinimumIsolation = configured }
            }
        };
        var resolver = BuildResolver(config, ("container_tool", ContainerTool()));

        var profile = resolver.Resolve("container_tool");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }
}
