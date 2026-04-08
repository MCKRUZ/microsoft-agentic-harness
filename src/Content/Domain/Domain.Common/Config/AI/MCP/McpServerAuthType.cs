namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Authentication type for MCP server connections.
/// </summary>
public enum McpServerAuthType
{
    /// <summary>
    /// No authentication required. Typical for local stdio-based servers.
    /// </summary>
    None,

    /// <summary>
    /// API key authentication via HTTP header.
    /// Key is sent in a configurable header (default: X-API-Key).
    /// </summary>
    ApiKey,

    /// <summary>
    /// Bearer token authentication.
    /// Token is sent in the Authorization header as "Bearer {token}".
    /// </summary>
    Bearer,

    /// <summary>
    /// Microsoft Entra ID (Azure AD) authentication.
    /// Uses OAuth 2.0 client credentials flow to obtain access tokens.
    /// </summary>
    Entra
}
