namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Configuration for a single MCP server instance. Supports stdio, SSE,
/// and HTTP transports with optional authentication.
/// </summary>
/// <remarks>
/// <para>
/// Example appsettings.json:
/// <code>
/// "McpServers": {
///   "Servers": {
///     "filesystem": {
///       "Type": "Stdio",
///       "Command": "npx",
///       "Args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
///       "Description": "File system access"
///     },
///     "remote-tools": {
///       "Type": "Http",
///       "Url": "https://tools.example.com/mcp",
///       "Auth": { "Type": "Bearer", "BearerToken": "${MCP_TOKEN}" },
///       "Description": "Remote tool server"
///     }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public class McpServerDefinition
{
    /// <summary>Whether this MCP server is enabled.</summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>The transport type for this MCP server connection.</summary>
    /// <value>Default: <see cref="McpServerType.Stdio"/>.</value>
    public McpServerType Type { get; set; } = McpServerType.Stdio;

    /// <summary>
    /// For stdio servers: the command to execute (e.g., "npx", "node", "dotnet").
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>For stdio servers: command arguments.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Environment variables to set for the MCP server process.</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>For SSE/HTTP servers: the server URL.</summary>
    public string? Url { get; set; }

    /// <summary>Working directory for the MCP server process.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Timeout in seconds for server startup.</summary>
    /// <value>Default: 30 seconds.</value>
    public int StartupTimeoutSeconds { get; set; } = 30;

    /// <summary>Description of what this MCP server provides.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Authentication configuration for HTTP/SSE-based MCP servers.
    /// Not applicable for stdio servers.
    /// </summary>
    public McpServerAuthConfig? Auth { get; set; }

    /// <summary>Gets whether this server requires authentication.</summary>
    public bool RequiresAuth => Auth?.IsConfigured ?? false;

    /// <summary>Gets whether this is a remote (HTTP/SSE) server.</summary>
    public bool IsRemoteServer => Type is McpServerType.Http or McpServerType.Sse;
}
