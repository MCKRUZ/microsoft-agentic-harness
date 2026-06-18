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
    /// Gets or sets the OAuth 2.0 scopes to request when minting a token for
    /// <see cref="McpServerAuthType.Entra"/> authentication.
    /// </summary>
    /// <remarks>
    /// <strong>Required for Entra.</strong> Identifies the resource the harness mints a
    /// token for — typically the target server's application ID URI plus <c>/.default</c>
    /// (e.g. <c>api://&lt;app-id&gt;/.default</c>). At least one scope must be supplied;
    /// without it the harness cannot request a token, so an Entra server configured with
    /// no scope is treated as invalid rather than connecting with no credential.
    /// </remarks>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Gets whether authentication is configured.</summary>
    public bool IsConfigured => Type != McpServerAuthType.None;

    /// <summary>Gets whether the configuration is valid for the selected type.</summary>
    /// <remarks>
    /// For <see cref="McpServerAuthType.Entra"/> the harness mints its own short-lived,
    /// auto-rotating token rather than forwarding a caller's credential. The preferred,
    /// secure-by-default shape is <em>managed identity</em>: supply only <see cref="Scopes"/>
    /// (and optionally <see cref="ClientId"/> for a user-assigned identity) and leave
    /// <see cref="ClientSecret"/> / <see cref="CertificatePath"/> unset — no standing secret
    /// is stored. A client secret or certificate is supported as an explicit fallback but
    /// requires both <see cref="TenantId"/> and <see cref="ClientId"/>. Setting
    /// <see cref="TenantId"/> with no secret or certificate is rejected as a half-configured
    /// fallback, so a forgotten credential fails loudly rather than silently minting a token
    /// from an ambient identity.
    /// </remarks>
    public bool IsValid => Type switch
    {
        McpServerAuthType.None => true,
        McpServerAuthType.ApiKey => !string.IsNullOrWhiteSpace(ApiKey),
        McpServerAuthType.Bearer => !string.IsNullOrWhiteSpace(BearerToken),
        McpServerAuthType.Entra => Scopes.Count > 0 && IsEntraCredentialShapeValid,
        _ => false
    };

    /// <summary>
    /// Gets whether the Entra credential fields form one of the three supported shapes:
    /// managed identity (no secret and no certificate), client secret
    /// (<see cref="TenantId"/> + <see cref="ClientId"/> + <see cref="ClientSecret"/>),
    /// or certificate (<see cref="TenantId"/> + <see cref="ClientId"/> + <see cref="CertificatePath"/>).
    /// </summary>
    private bool IsEntraCredentialShapeValid
    {
        get
        {
            var hasSecret = !string.IsNullOrWhiteSpace(ClientSecret);
            var hasCertificate = !string.IsNullOrWhiteSpace(CertificatePath);

            if (!hasSecret && !hasCertificate)
            {
                // Managed identity / default credential chain — no standing secret to leak.
                // A ClientId is allowed (user-assigned managed identity), but a TenantId with
                // no secret or certificate signals a half-configured client-secret/certificate
                // shape — reject it so the misconfiguration fails loudly rather than silently
                // minting a token from a different (ambient) credential.
                return string.IsNullOrWhiteSpace(TenantId);
            }

            // Client secret or certificate fallback — both require tenant + client id.
            return !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
        }
    }
}
