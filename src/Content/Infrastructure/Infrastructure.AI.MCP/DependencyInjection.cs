using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MCP;

/// <summary>
/// Dependency injection configuration for the Infrastructure.AI.MCP layer.
/// Registers MCP client connection management and tool provider services.
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root:
/// <code>
/// services.AddMcpClientDependencies();
/// </code>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all MCP client dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMcpClientDependencies(this IServiceCollection services)
    {
        // Connection manager — singleton, manages MCP client lifecycles
        services.AddSingleton<McpConnectionManager>(sp =>
        {
            var appConfig = sp.GetRequiredService<IOptions<AppConfig>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpConnectionManager>>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new McpConnectionManager(logger, loggerFactory, appConfig.Value.AI.McpServers);
        });

        // Tool provider — singleton wrapping connection manager
        services.AddSingleton<IMcpToolProvider, McpToolProvider>();

        return services;
    }
}
