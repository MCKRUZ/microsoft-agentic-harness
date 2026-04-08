namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Transport type for MCP server connections.
/// </summary>
public enum McpServerType
{
    /// <summary>
    /// Stdio-based MCP server — spawns a process and communicates via stdin/stdout.
    /// Typical for local tools (e.g., <c>npx @modelcontextprotocol/server-filesystem</c>).
    /// </summary>
    Stdio,

    /// <summary>
    /// Server-Sent Events (SSE) based MCP server.
    /// </summary>
    Sse,

    /// <summary>
    /// HTTP-based MCP server (Streamable HTTP transport).
    /// </summary>
    Http
}
