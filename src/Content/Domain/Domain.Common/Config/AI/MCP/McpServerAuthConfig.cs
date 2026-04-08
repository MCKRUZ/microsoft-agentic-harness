namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Authentication configuration for MCP server connections.
/// Supports API key, bearer token, and Entra ID authentication methods.
/// </summary>
public class McpServerAuthConfig
{
    /// <summary>
    /// Gets or sets the authentication type.
    /// </summary>
    public McpServerAuthType Type { get; set; } = McpServerAuthType.None;

    /// <summary>
    /// Gets or sets the API key for <see cref="McpServerAuthType.ApiKey"/> authentication.
    /// </summary>
    /// <remarks>
    /// <strong>Do not hardcode in appsettings.json.</strong>
    /// Use environment variables or Azure Key Vault.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the HTTP header name for API key transmission.
    /// </summary>
    /// <value>Default: "X-API-Key".</value>
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>
    /// Gets or sets the bearer token for <see cref="McpServerAuthType.Bearer"/> authentication.
    /// </summary>
    /// <remarks>
    /// <strong>Do not hardcode in appsettings.json.</strong>
    /// Use environment variables or Azure Key Vault.
    /// </remarks>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Gets or sets the Entra ID tenant ID.
    /// Required for <see cref="McpServerAuthType.Entra"/> authentication.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the Entra ID client ID (application ID).
    /// Required for <see cref="McpServerAuthType.Entra"/> authentication.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the Entra ID client secret.
    /// Either this or <see cref="CertificatePath"/> is required
    /// for <see cref="McpServerAuthType.Entra"/> authentication.
    /// </summary>
    /// <remarks>
    /// <strong>Do not hardcode in appsettings.json.</strong>
    /// Use environment variables or Azure Key Vault.
    /// </remarks>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the path to the client certificate for Entra ID authentication.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Gets or sets the OAuth 2.0 scopes to request.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Gets whether authentication is configured.</summary>
    public bool IsConfigured => Type != McpServerAuthType.None;

    /// <summary>Gets whether the configuration is valid for the selected type.</summary>
    public bool IsValid => Type switch
    {
        McpServerAuthType.None => true,
        McpServerAuthType.ApiKey => !string.IsNullOrWhiteSpace(ApiKey),
        McpServerAuthType.Bearer => !string.IsNullOrWhiteSpace(BearerToken),
        McpServerAuthType.Entra => !string.IsNullOrWhiteSpace(TenantId)
            && !string.IsNullOrWhiteSpace(ClientId)
            && (!string.IsNullOrWhiteSpace(ClientSecret) || !string.IsNullOrWhiteSpace(CertificatePath)),
        _ => false
    };
}
