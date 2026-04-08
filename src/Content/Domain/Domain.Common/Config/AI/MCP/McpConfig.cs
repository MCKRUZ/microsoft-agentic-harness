namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Server-side MCP configuration — used when this application acts as an MCP server
/// that accepts incoming connections from external agents/clients.
/// </summary>
/// <remarks>
/// <para>
/// This is separate from <see cref="McpServersConfig"/> which defines external
/// MCP servers to connect to as a client. This class configures authentication
/// for when YOUR application is the MCP server.
/// </para>
/// </remarks>
public class McpConfig
{
    /// <summary>
    /// Gets or sets the server name exposed in MCP protocol handshake.
    /// </summary>
    /// <value>Default: "agentic-harness".</value>
    public string ServerName { get; set; } = "agentic-harness";

    /// <summary>
    /// Gets or sets the server version exposed in MCP protocol handshake.
    /// </summary>
    /// <value>Default: "1.0.0".</value>
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Gets or sets the server instructions sent to clients during initialization.
    /// </summary>
    public string ServerInstructions { get; set; } = "Agentic harness MCP server — provides tools, prompts, and resources for AI agents.";

    /// <summary>
    /// Gets or sets the initialization timeout.
    /// </summary>
    /// <value>Default: 60 seconds.</value>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the Azure Entra ID configuration for validating incoming OAuth tokens.
    /// </summary>
    public McpServerAuthConfig Auth { get; set; } = new();

    /// <summary>
    /// Gets or sets the assembly names to scan for MCP tools, prompts, and resources.
    /// </summary>
    public List<string> ScanAssemblies { get; set; } = [];
}
