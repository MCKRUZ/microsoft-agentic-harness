using Microsoft.AspNetCore.Authentication;

namespace Infrastructure.AI.MCPServer.Authentication;

/// <summary>
/// Options for <see cref="McpSharedKeyAuthenticationHandler"/> — shared-secret
/// authentication for inbound MCP requests via API key header or static bearer token.
/// </summary>
public sealed class McpSharedKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Gets or sets the HTTP header carrying the credential
    /// (e.g. <c>X-API-Key</c> or <c>Authorization</c>).
    /// </summary>
    public string HeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// Gets or sets the auth-scheme prefix of the header value, stripped before
    /// comparison. Empty for raw API keys; <c>"Bearer "</c> for the static bearer
    /// scheme. Per RFC 7235 the scheme token is matched case-insensitively and any
    /// amount of whitespace between scheme and credential is tolerated.
    /// </summary>
    public string ValuePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expected credential inbound requests are compared against.
    /// Sourced from configuration (User Secrets / Key Vault) — never hardcoded.
    /// </summary>
    public string ExpectedCredential { get; set; } = string.Empty;

    /// <summary>
    /// Validates that the scheme has usable key material, so a half-configured scheme
    /// fails at startup rather than silently rejecting (or worse, accepting) requests.
    /// </summary>
    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrWhiteSpace(HeaderName))
            throw new InvalidOperationException(
                $"{nameof(McpSharedKeyAuthenticationOptions)}.{nameof(HeaderName)} must be set.");

        if (string.IsNullOrWhiteSpace(ExpectedCredential))
            throw new InvalidOperationException(
                $"{nameof(McpSharedKeyAuthenticationOptions)}.{nameof(ExpectedCredential)} must be set. " +
                "Configure the shared credential via AppConfig:AI:MCP:Auth (User Secrets or Key Vault).");
    }
}
