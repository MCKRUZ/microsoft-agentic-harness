using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

public sealed class McpConnectionManagerTests
{
    private static McpConnectionManager CreateManager(McpServersConfig? config = null)
    {
        return new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            config ?? new McpServersConfig());
    }

    [Fact]
    public void GetConfiguredServerNames_NoServers_ReturnsEmpty()
    {
        var sut = CreateManager();

        var names = sut.GetConfiguredServerNames();

        names.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguredServerNames_WithEnabledServers_ReturnsTheirNames()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["filesystem"] = new() { Enabled = true },
                ["disabled-server"] = new() { Enabled = false },
                ["github"] = new() { Enabled = true }
            }
        };
        var sut = CreateManager(config);

        var names = sut.GetConfiguredServerNames().ToList();

        names.Should().BeEquivalentTo("filesystem", "github");
        names.Should().NotContain("disabled-server");
    }

    [Fact]
    public void IsConnected_NoConnections_ReturnsFalse()
    {
        var sut = CreateManager();

        sut.IsConnected("any-server").Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_EmptyManager_DoesNotThrow()
    {
        var sut = CreateManager();

        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var sut = CreateManager();

        await sut.DisposeAsync();
        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetClientAsync_UnconfiguredServer_ThrowsMcpConnectionException()
    {
        var sut = CreateManager();

        var act = () => sut.GetClientAsync("nonexistent");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_DisabledServer_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["disabled"] = new() { Enabled = false }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("disabled");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }
}
