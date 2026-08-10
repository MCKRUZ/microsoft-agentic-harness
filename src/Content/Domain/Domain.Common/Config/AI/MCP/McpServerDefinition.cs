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

    /// <summary>
    /// Whether this server's tool behaviour annotations may be believed when they <em>reduce</em>
    /// friction — specifically, whether a tool it marks read-only is exempt from the non-read-only
    /// approval posture. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not simply on.</strong> A tool annotation is supplied by the party the
    /// annotation is used to police. The MCP specification is explicit that annotations "are not
    /// guaranteed to provide a faithful description of tool behavior" and that clients "should never
    /// make tool use decisions based on <c>ToolAnnotations</c> received from untrusted servers": a
    /// server wanting to escape an approval gate marks its destructive tool read-only and walks
    /// through. Connecting a server is a decision about where its tools come from; this is the
    /// separate decision about whether to take its word for what they do.
    /// </para>
    /// <para>
    /// <strong>It only ever loosens.</strong> Annotations that make an outcome stricter — a tool
    /// declaring itself destructive — are honoured from every server regardless of this flag, because
    /// a server with an incentive to lie has no incentive to lie in that direction.
    /// </para>
    /// <para>
    /// Set this true for servers whose code the operator controls or whose publisher they have
    /// assessed. Leaving it false on a server means every tool it offers requires approval while the
    /// posture is on, which is the correct cost of not knowing.
    /// </para>
    /// </remarks>
    public bool TrustToolAnnotations { get; set; }

    /// <summary>Gets whether this server requires authentication.</summary>
    public bool RequiresAuth => Auth?.IsConfigured ?? false;

    /// <summary>Gets whether this is a remote (HTTP/SSE) server.</summary>
    public bool IsRemoteServer => Type is McpServerType.Http or McpServerType.Sse;
}
