using Application.AI.Common.Exceptions;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for <see cref="McpConnectionManager"/> transport creation paths
/// covering HTTP auth header injection, SSE transport, and concurrent access.
/// </summary>
public sealed class McpConnectionManagerTransportTests
{
    private static McpConnectionManager CreateManager(McpServersConfig? config = null)
    {
        return new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            config ?? new McpServersConfig());
    }

    // -- SSE transport --

    [Fact]
    public async Task GetClientAsync_SseServerWithNoUrl_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["sse-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Sse,
                    Url = null,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("sse-test");

        await act.Should().ThrowAsync<McpConnectionException>();
    }

    // -- HTTP with auth --

    [Fact]
    public async Task GetClientAsync_HttpWithApiKeyAuth_ThrowsOnConnection()
    {
        // The transport is created but connection to a fake URL will fail.
        // This tests that CreateHttpTransport doesn't throw during construction.
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["api-key-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.ApiKey,
                        ApiKey = "test-api-key",
                        ApiKeyHeader = "X-API-Key"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("api-key-server");

        // Will throw McpConnectionException wrapping the actual transport failure
        await act.Should().ThrowAsync<McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_HttpWithBearerAuth_ThrowsOnConnection()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["bearer-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.Bearer,
                        BearerToken = "test-bearer-token"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("bearer-server");

        await act.Should().ThrowAsync<McpConnectionException>();
    }

    // -- Concurrent GetClientAsync --

    [Fact]
    public async Task GetClientAsync_ConcurrentCallsSameServer_ThrowsSameException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["concurrent-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Assert.ThrowsAsync<McpConnectionException>(
                () => sut.GetClientAsync("concurrent-test")));

        var exceptions = await Task.WhenAll(tasks);

        exceptions.Should().AllBeOfType<McpConnectionException>();
    }

    // -- Unsupported transport type --

    [Fact]
    public async Task GetClientAsync_UnsupportedType_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["bad-type"] = new()
                {
                    Enabled = true,
                    Type = (McpServerType)999,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("bad-type");

        await act.Should().ThrowAsync<McpConnectionException>()
            .WithMessage("*Unsupported*");
    }

    // -- Stdio with environment variables --

    [Fact]
    public async Task GetClientAsync_StdioWithEnvVars_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerDefinition>
            {
                ["env-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "nonexistent-binary",
                    StartupTimeoutSeconds = 1,
                    Env = new Dictionary<string, string>
                    {
                        ["TEST_VAR"] = "test-value"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("env-test");

        await act.Should().ThrowAsync<McpConnectionException>();
    }
}
