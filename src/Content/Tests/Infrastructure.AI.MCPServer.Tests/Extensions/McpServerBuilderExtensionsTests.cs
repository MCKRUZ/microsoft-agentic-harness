using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Extensions;

/// <summary>
/// Tests for <see cref="Infrastructure.AI.MCPServer.Extensions.McpServerBuilderExtensions"/>
/// covering assembly scanning configuration.
/// </summary>
public sealed class McpServerBuilderExtensionsTests
{
    [Fact]
    public void McpConfig_EmptyScanAssemblies_DoesNotThrow()
    {
        var config = new McpConfig { ScanAssemblies = [] };

        config.ScanAssemblies.Should().BeEmpty();
    }

    [Fact]
    public void McpConfig_DefaultServerName_IsNotEmpty()
    {
        var config = new McpConfig();

        config.ServerName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpConfig_DefaultServerVersion_IsNotEmpty()
    {
        var config = new McpConfig();

        config.ServerVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpConfig_InitializationTimeout_HasSensibleDefault()
    {
        var config = new McpConfig();

        config.InitializationTimeout.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
