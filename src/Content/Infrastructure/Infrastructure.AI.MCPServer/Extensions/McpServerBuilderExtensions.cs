using Domain.Common.Config.AI.MCP;
using ModelContextProtocol.Server;
using System.Reflection;

namespace Infrastructure.AI.MCPServer.Extensions;

/// <summary>
/// Extension methods for loading MCP tools, prompts, and resources
/// from configured assemblies via reflection.
/// </summary>
public static class McpServerBuilderExtensions
{
    /// <summary>
    /// Loads MCP tools from all assemblies configured in
    /// <see cref="McpConfig.ScanAssemblies"/>.
    /// </summary>
    public static IMcpServerBuilder LoadToolsFromAssemblies(
        this IMcpServerBuilder builder, McpConfig config)
        => LoadFromAssemblies(builder, config, (b, a) => b.WithToolsFromAssembly(a));

    /// <summary>
    /// Loads MCP prompts from all assemblies configured in
    /// <see cref="McpConfig.ScanAssemblies"/>.
    /// </summary>
    public static IMcpServerBuilder LoadPromptsFromAssemblies(
        this IMcpServerBuilder builder, McpConfig config)
        => LoadFromAssemblies(builder, config, (b, a) => b.WithPromptsFromAssembly(a));

    /// <summary>
    /// Loads MCP resources from all assemblies configured in
    /// <see cref="McpConfig.ScanAssemblies"/>.
    /// </summary>
    public static IMcpServerBuilder LoadResourcesFromAssemblies(
        this IMcpServerBuilder builder, McpConfig config)
        => LoadFromAssemblies(builder, config, (b, a) => b.WithResourcesFromAssembly(a));

    private static IMcpServerBuilder LoadFromAssemblies(
        IMcpServerBuilder builder, McpConfig config,
        Func<IMcpServerBuilder, Assembly, IMcpServerBuilder> loader)
    {
        foreach (var assemblyName in config.ScanAssemblies)
        {
            loader(builder, Assembly.Load(assemblyName));
        }

        return builder;
    }
}
