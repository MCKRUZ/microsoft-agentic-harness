using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Integration tests for <see cref="McpToolProvider"/> covering tool discovery,
/// dispose behavior, and resilience when MCP servers are unavailable.
/// </summary>
public sealed class McpToolProviderIntegrationTests
{
    private static (McpToolProvider Provider, McpConnectionManager Manager) CreateProvider(
        McpServersConfig? config = null)
    {
        var manager = new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            config ?? new McpServersConfig());

        var provider = new McpToolProvider(
            Mock.Of<ILogger<McpToolProvider>>(),
            manager);

        return (provider, manager);
    }

    // -- GetToolsAsync --

    [Fact]
    public async Task GetToolsAsync_UnavailableServer_ReturnsEmptyList()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["bad-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var (provider, _) = CreateProvider(config);

        var tools = await provider.GetToolsAsync("bad-server");

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task GetToolsAsync_UnconfiguredServer_ReturnsEmptyList()
    {
        var (provider, _) = CreateProvider();

        var tools = await provider.GetToolsAsync("nonexistent");

        tools.Should().BeEmpty();
    }

    // -- GetAllToolsAsync --

    [Fact]
    public async Task GetAllToolsAsync_NoServersConfigured_ReturnsEmptyDictionary()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.GetAllToolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllToolsAsync_AllServersUnavailable_ReturnsEmptyDictionary()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["server-a"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                },
                ["server-b"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var (provider, _) = CreateProvider(config);

        var result = await provider.GetAllToolsAsync();

        result.Should().BeEmpty();
    }

    // -- GetToolByNameAsync --

    [Fact]
    public async Task GetToolByNameAsync_NoServers_ReturnsNull()
    {
        var (provider, _) = CreateProvider();

        var result = await provider.GetToolByNameAsync("any-tool");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetToolByNameAsync_UnavailableServers_ReturnsNull()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["broken"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var (provider, _) = CreateProvider(config);

        var result = await provider.GetToolByNameAsync("some-tool");

        result.Should().BeNull();
    }

    // -- IsServerAvailableAsync --

    [Fact]
    public async Task IsServerAvailableAsync_UnconfiguredServer_ReturnsFalse()
    {
        var (provider, _) = CreateProvider();

        var available = await provider.IsServerAvailableAsync("nonexistent");

        available.Should().BeFalse();
    }

    [Fact]
    public async Task IsServerAvailableAsync_DisabledServer_ReturnsFalse()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["disabled"] = new() { Enabled = false }
            }
        };
        var (provider, _) = CreateProvider(config);

        var available = await provider.IsServerAvailableAsync("disabled");

        available.Should().BeFalse();
    }

    // -- Dispose --

    [Fact]
    public async Task GetToolsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (provider, _) = CreateProvider();
        provider.Dispose();

        var act = () => provider.GetToolsAsync("any");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetAllToolsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (provider, _) = CreateProvider();
        provider.Dispose();

        var act = () => provider.GetAllToolsAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetToolByNameAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (provider, _) = CreateProvider();
        provider.Dispose();

        var act = () => provider.GetToolByNameAsync("any");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task IsServerAvailableAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (provider, _) = CreateProvider();
        provider.Dispose();

        var act = () => provider.IsServerAvailableAsync("any");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var (provider, _) = CreateProvider();

        var act = () =>
        {
            provider.Dispose();
            provider.Dispose();
        };

        act.Should().NotThrow();
    }
}
