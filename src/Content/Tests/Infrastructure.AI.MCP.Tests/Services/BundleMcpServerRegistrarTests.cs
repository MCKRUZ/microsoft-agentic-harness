using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Bundles;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for <see cref="BundleMcpServerRegistrar"/> — the deregistration counterpart to the bundle MCP
/// server registration <c>BundleStagingService</c> performs at staging time (issue #368).
/// </summary>
public sealed class BundleMcpServerRegistrarTests
{
    private static BundleMcpServerRegistrar CreateSut(BundleOwnedMcpServerRegistry bundleOwned)
    {
        return new(
            bundleOwned,
            McpConnectionManagerBundleEgressSupport.CreateManager(
                Mock.Of<ILogger<McpConnectionManager>>(),
                new Mock<ILoggerFactory>().Object,
                TestSsrf.HandlerFactory(),
                new McpServersConfig(),
                bundleOwned),
            Mock.Of<ILogger<BundleMcpServerRegistrar>>());
    }

    [Fact]
    public async Task DeregisterAsync_RemovesNamedServers_LeavesOthersUntouched()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:echo", new() { Command = "npx" });
        bundleOwned.TryAdd("b2:other", new() { Command = "node" });
        var sut = CreateSut(bundleOwned);

        await sut.DeregisterAsync(["b1:echo"]);

        bundleOwned.TryGetValue("b1:echo", out _).Should().BeFalse();
        bundleOwned.TryGetValue("b2:other", out _).Should().BeTrue("deregistration must only ever touch the named entries");
    }

    [Fact]
    public async Task DeregisterAsync_UnknownServerName_IsANoOpAndDoesNotThrow()
    {
        var sut = CreateSut(new BundleOwnedMcpServerRegistry());

        var act = async () => await sut.DeregisterAsync(["never-registered"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeregisterAsync_EmptyList_IsANoOp()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:echo", new() { Command = "npx" });
        var sut = CreateSut(bundleOwned);

        await sut.DeregisterAsync([]);

        bundleOwned.TryGetValue("b1:echo", out _).Should().BeTrue();
    }
}
