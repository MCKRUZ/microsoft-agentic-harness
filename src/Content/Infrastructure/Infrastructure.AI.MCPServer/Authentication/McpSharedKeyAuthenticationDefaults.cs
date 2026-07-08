namespace Infrastructure.AI.MCPServer.Authentication;

/// <summary>
/// Authentication scheme names for the MCP server's shared-key schemes
/// (<see cref="Domain.Common.Config.AI.MCP.McpServerAuthType.ApiKey"/> and
/// <see cref="Domain.Common.Config.AI.MCP.McpServerAuthType.Bearer"/>).
/// </summary>
public static class McpSharedKeyAuthenticationDefaults
{
    /// <summary>
    /// Scheme validating an API key carried in a configurable header
    /// (default <c>X-API-Key</c>).
    /// </summary>
    public const string ApiKeyScheme = "McpApiKey";

    /// <summary>
    /// Scheme validating a static shared token carried as
    /// <c>Authorization: Bearer {token}</c>. Distinct from JWT bearer — the token is a
    /// pre-shared secret compared in constant time, not a signed claim set.
    /// </summary>
    public const string BearerScheme = "McpSharedBearer";
}
