using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Extended tests for <see cref="McpConnectionManager"/> covering dispose behavior,
/// disconnect operations, transport creation edge cases, and concurrent access patterns.
/// </summary>
public sealed class McpConnectionManagerExtendedTests
{
    private static McpConnectionManager CreateManager(McpServersConfig? config = null)
    {
        return new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            config ?? new McpServersConfig());
    }

    // -- GetClientAsync after dispose --

    [Fact]
    public async Task GetClientAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateManager();
        await sut.DisposeAsync();

        var act = () => sut.GetClientAsync("any-server");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // -- DisconnectAsync --

    [Fact]
    public async Task DisconnectAsync_NonexistentServer_DoesNotThrow()
    {
        var sut = CreateManager();

        var act = async () => await sut.DisconnectAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    // -- GetConfiguredServerNames edge cases --

    [Fact]
    public void GetConfiguredServerNames_AllDisabled_ReturnsEmpty()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["a"] = new() { Enabled = false },
                ["b"] = new() { Enabled = false }
            }
        };
        var sut = CreateManager(config);

        sut.GetConfiguredServerNames().Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguredServerNames_MixedEnabled_ReturnsOnlyEnabled()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["enabled-1"] = new() { Enabled = true },
                ["disabled-1"] = new() { Enabled = false },
                ["enabled-2"] = new() { Enabled = true },
                ["disabled-2"] = new() { Enabled = false },
                ["enabled-3"] = new() { Enabled = true }
            }
        };
        var sut = CreateManager(config);

        var names = sut.GetConfiguredServerNames().ToList();

        names.Should().HaveCount(3);
        names.Should().BeEquivalentTo("enabled-1", "enabled-2", "enabled-3");
    }

    // -- IsConnected --

    [Fact]
    public void IsConnected_EmptyConfig_ReturnsFalse()
    {
        var sut = CreateManager();

        sut.IsConnected("anything").Should().BeFalse();
    }

    // -- GetClientAsync with invalid server config --

    [Fact]
    public async Task GetClientAsync_StdioServerWithNoCommand_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["stdio-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("stdio-test");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_HttpServerWithNoUrl_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["http-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = null,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("http-test");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }

    // -- DisposeAsync with connection locks --

    [Fact]
    public async Task DisposeAsync_AfterMultipleGetAttempts_CleansUpLocks()
    {
        var sut = CreateManager();

        // Try to get a non-existent server (will throw), then dispose
        try { await sut.GetClientAsync("a"); } catch { }
        try { await sut.GetClientAsync("b"); } catch { }

        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
