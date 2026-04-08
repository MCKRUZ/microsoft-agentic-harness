namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Client-side configuration for external MCP servers the harness connects to.
/// Each entry defines a server the agent can consume tools from.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by server name (e.g., "filesystem", "github", "remote-tools").
/// The key becomes the server identifier used in <c>IMcpToolProvider.GetToolsAsync(serverName)</c>.
/// </para>
/// </remarks>
public class McpServersConfig
{
    /// <summary>
    /// Gets or sets the dictionary of MCP server definitions keyed by server name.
    /// </summary>
    public Dictionary<string, McpServerDefinition> Servers { get; set; } = new();
}
