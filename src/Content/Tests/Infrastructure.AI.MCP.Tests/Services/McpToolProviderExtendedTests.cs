using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Extended tests for <see cref="McpToolProvider"/> covering dispose gating
/// on all public methods and edge cases with configured servers.
/// </summary>
public sealed class McpToolProviderExtendedTests
{
    private static (McpToolProvider Provider, McpConnectionManager Manager) CreateSut(
        McpServersConfig? config = null)
    {
        var manager = new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            config ?? new McpServersConfig());

        var provider = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(), manager);

        return (provider, manager);
    }

    [Fact]
    public async Task GetAllToolsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (sut, _) = CreateSut();
        sut.Dispose();

        var act = () => sut.GetAllToolsAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task IsServerAvailableAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (sut, _) = CreateSut();
        sut.Dispose();

        var act = () => sut.IsServerAvailableAsync("any");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetToolByNameAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (sut, _) = CreateSut();
        sut.Dispose();

        var act = () => sut.GetToolByNameAsync("any-tool");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetToolByNameAsync_NoConfiguredServers_ReturnsNull()
    {
        var (sut, _) = CreateSut();

        var result = await sut.GetToolByNameAsync("nonexistent-tool");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllToolsAsync_ConfiguredServersFail_ReturnsEmpty()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["broken"] = new() { Enabled = true, Type = McpServerType.Stdio, Command = "" }
            }
        };
        var (sut, _) = CreateSut(config);

        var result = await sut.GetAllToolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetToolByNameAsync_AllServersFail_ReturnsNull()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["broken"] = new() { Enabled = true, Type = McpServerType.Stdio, Command = "" }
            }
        };
        var (sut, _) = CreateSut(config);

        var result = await sut.GetToolByNameAsync("some-tool");

        result.Should().BeNull();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var (sut, _) = CreateSut();

        var act = () =>
        {
            sut.Dispose();
            sut.Dispose();
            sut.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task IsServerAvailableAsync_UnconfiguredServer_ReturnsFalse()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["only-server"] = new() { Enabled = true, Type = McpServerType.Stdio, Command = "" }
            }
        };
        var (sut, _) = CreateSut(config);

        var result = await sut.IsServerAvailableAsync("different-server");

        result.Should().BeFalse();
    }
}
