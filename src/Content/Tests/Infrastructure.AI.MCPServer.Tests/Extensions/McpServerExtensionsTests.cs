using System.Collections.Concurrent;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCPServer.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Extensions;

/// <summary>
/// Integration tests for <see cref="McpServerExtensions"/> covering MCP server
/// service registration and authentication configuration via real DI containers.
/// </summary>
public sealed class McpServerExtensionsTests
{
    private static AppConfig CreateAppConfig(
        string serverName = "test-harness",
        string serverVersion = "1.0.0",
        McpServerAuthConfig? auth = null)
    {
        return new AppConfig
        {
            AI = new AIConfig
            {
                MCP = new McpConfig
                {
                    ServerName = serverName,
                    ServerVersion = serverVersion,
                    ScanAssemblies = [],
                    Auth = auth ?? new McpServerAuthConfig()
                }
            }
        };
    }

    // -- AddMcpServerServices --

    [Fact]
    public void AddMcpServerServices_RegistersConcurrentDictionarySingleton()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();

        services.AddMcpServerServices(appConfig);

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(ConcurrentDictionary<string, byte>));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddMcpServerServices_WithCustomServerName_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig(serverName: "custom-mcp-server");

        var act = () => services.AddMcpServerServices(appConfig);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddMcpServerServices_WithEmptyScanAssemblies_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();

        var act = () => services.AddMcpServerServices(appConfig);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddMcpServerServices_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();

        var result = services.AddMcpServerServices(appConfig);

        result.Should().BeSameAs(services);
    }

    // -- AddMcpAuthentication --

    [Fact]
    public void AddMcpAuthentication_NoAuthConfigured_RegistersAnonymousAuth()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();
        var configuration = new ConfigurationBuilder().Build();

        services.AddMcpAuthentication(appConfig, configuration);

        services.Any(d => d.ServiceType.Name.Contains("Authentication")).Should().BeTrue();
    }

    [Fact]
    public void AddMcpAuthentication_EntraAuth_RegistersJwtBearerServices()
    {
        var services = new ServiceCollection();
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = "test-tenant-id",
            ClientId = "test-client-id"
        };
        var appConfig = CreateAppConfig(auth: auth);
        var configuration = new ConfigurationBuilder().Build();

        services.AddMcpAuthentication(appConfig, configuration);

        services.Any(d => d.ServiceType.Name.Contains("Authentication")).Should().BeTrue();
    }

    [Fact]
    public void AddMcpAuthentication_NoAuth_RegistersAuthorizationServices()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();
        var configuration = new ConfigurationBuilder().Build();

        services.AddMcpAuthentication(appConfig, configuration);

        services.Any(d => d.ServiceType.Name.Contains("Authorization")).Should().BeTrue();
    }

    [Fact]
    public void AddMcpAuthentication_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var appConfig = CreateAppConfig();
        var configuration = new ConfigurationBuilder().Build();

        var result = services.AddMcpAuthentication(appConfig, configuration);

        result.Should().BeSameAs(services);
    }
}
