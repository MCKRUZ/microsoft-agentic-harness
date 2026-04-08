using Domain.Common.Config.AI.MCP;

namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for AI services including MCP server/client settings,
/// agent framework, and model selection.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI
/// ├── MCP        — Server-side MCP configuration (auth, tool assemblies)
/// └── McpServers — Client-side MCP server registry (external servers to connect to)
/// </code>
/// </para>
/// </remarks>
public class AIConfig
{
    /// <summary>
    /// Gets or sets the MCP server-side configuration (when this app is the server).
    /// </summary>
    public McpConfig MCP { get; set; } = new();

    /// <summary>
    /// Gets or sets the MCP client-side server registry (external servers to consume).
    /// </summary>
    public McpServersConfig McpServers { get; set; } = new();
}
