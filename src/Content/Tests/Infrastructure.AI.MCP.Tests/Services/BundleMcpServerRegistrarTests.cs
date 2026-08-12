using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
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
    private static BundleMcpServerRegistrar CreateSut(McpServersConfig config) =>
        new(
            config,
            new McpConnectionManager(
                Mock.Of<ILogger<McpConnectionManager>>(),
                new Mock<ILoggerFactory>().Object,
                TestSsrf.HandlerFactory(),
                config),
            Mock.Of<ILogger<BundleMcpServerRegistrar>>());

    [Fact]
    public async Task DeregisterAsync_RemovesNamedServers_LeavesOthersUntouched()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["b1:echo"] = new() { Command = "npx" },
                ["host-configured"] = new() { Command = "node" }
            }
        };
        var sut = CreateSut(config);

        await sut.DeregisterAsync(["b1:echo"]);

        config.Servers.Should().NotContainKey("b1:echo");
        config.Servers.Should().ContainKey("host-configured", "deregistration must only ever touch the named entries");
    }

    [Fact]
    public async Task DeregisterAsync_UnknownServerName_IsANoOpAndDoesNotThrow()
    {
        var config = new McpServersConfig();
        var sut = CreateSut(config);

        var act = async () => await sut.DeregisterAsync(["never-registered"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeregisterAsync_EmptyList_IsANoOp()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition> { ["b1:echo"] = new() { Command = "npx" } }
        };
        var sut = CreateSut(config);

        await sut.DeregisterAsync([]);

        config.Servers.Should().ContainKey("b1:echo");
    }
}
