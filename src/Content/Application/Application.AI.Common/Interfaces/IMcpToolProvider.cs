using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Provides AI tools from external MCP (Model Context Protocol) servers.
/// Manages connections to configured MCP servers and converts their tools
/// into <see cref="AITool"/> instances for consumption by the agent harness.
/// </summary>
/// <remarks>
/// <para>
/// Implementations manage the lifecycle of MCP client connections based on
/// <c>AppConfig.AI.McpServers</c> configuration. Each configured server
/// is identified by name (e.g., "filesystem", "github", "remote-tools").
/// </para>
/// <para>
/// The agent orchestration loop calls <see cref="GetAllToolsAsync"/> at
/// session start to discover available tools, then includes them in the
/// tool surface sent to the LLM alongside keyed DI tools.
/// </para>
/// </remarks>
public interface IMcpToolProvider : IDisposable
{
    /// <summary>
    /// Gets AI tools from the specified MCP server.
    /// </summary>
    /// <param name="serverName">The MCP server name (e.g., "filesystem").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of AI tools from the server, or empty if not available.</returns>
    Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available AI tools from all configured and enabled MCP servers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of server name to list of AI tools.</returns>
    Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an MCP server is available and connected.
    /// </summary>
    /// <param name="serverName">The MCP server name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the server is available and responding.</returns>
    Task<bool> IsServerAvailableAsync(string serverName, CancellationToken cancellationToken = default);
}
