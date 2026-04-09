using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

public sealed class McpToolProviderTests
{
    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var manager = CreateConnectionManager();
        var logger = Mock.Of<ILogger<McpToolProvider>>();

        using var sut = new McpToolProvider(logger, manager);

        sut.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAllToolsAsync_NoConfiguredServers_ReturnsEmpty()
    {
        var manager = CreateConnectionManager();
        using var sut = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);

        var result = await sut.GetAllToolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetToolsAsync_NonexistentServer_ReturnsEmptyGracefully()
    {
        // McpToolProvider catches exceptions from McpConnectionManager and returns []
        var manager = CreateConnectionManager();
        using var sut = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);

        var tools = await sut.GetToolsAsync("nonexistent");

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task IsServerAvailableAsync_NonexistentServer_ReturnsFalse()
    {
        var manager = CreateConnectionManager();
        using var sut = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);

        var available = await sut.IsServerAvailableAsync("nonexistent");

        available.Should().BeFalse();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var manager = CreateConnectionManager();
        var sut = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetToolsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var manager = CreateConnectionManager();
        var sut = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);
        sut.Dispose();

        var act = () => sut.GetToolsAsync("any");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static McpConnectionManager CreateConnectionManager()
    {
        return new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            new Domain.Common.Config.AI.MCP.McpServersConfig());
    }
}
