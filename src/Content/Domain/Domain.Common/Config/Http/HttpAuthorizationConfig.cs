namespace Domain.Common.Config.Http;

/// <summary>
/// Configuration for HTTP endpoint authorization using API key authentication.
/// Enables dual-key API key validation for incoming HTTP requests, supporting
/// seamless key rotation via primary and secondary keys.
/// </summary>
/// <remarks>
/// Binds to <c>AppConfig:Http:Authorization</c> in appsettings.json.
/// Used by <see cref="Infrastructure.Common.Middleware.EndpointFilters.HttpAuthEndpointFilter"/>
/// to validate API keys on incoming requests.
/// <para>
/// <strong>Security:</strong> Store <see cref="AccessKey1"/> and <see cref="AccessKey2"/>
/// in User Secrets (development) or Azure Key Vault (production). Never commit keys
/// to source control.
/// </para>
/// <para>
/// Example configuration:
/// <code>
/// "Authorization": {
///   "Enabled": true,
///   "HttpHeaderName": "X-API-Key",
///   "AccessKey1": "primary-key-from-vault",
///   "AccessKey2": "secondary-key-for-rotation"
/// }
/// </code>
/// </para>
/// </remarks>
// Mutable setters required by IOptionsMonitor<T> binding. Treat as read-only after DI setup.
public class HttpAuthorizationConfig
{
    /// <summary>
    /// Gets or sets whether API key authorization is enabled.
    /// When <c>false</c>, the endpoint filter passes all requests through without validation.
    /// </summary>
    /// <value>Default: <c>false</c>.</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets the authentication type. Currently only <c>"APIKey"</c> is supported.
    /// </summary>
    /// <value>Always returns <c>"APIKey"</c>.</value>
    public string AuthenticationType => "APIKey";

    /// <summary>
    /// Gets or sets the HTTP header name used to transmit the API key.
    /// Common values: <c>"Authorization"</c>, <c>"X-API-Key"</c>, <c>"x-api-key"</c>.
    /// </summary>
    /// <value>Default: <c>"Authorization"</c>.</value>
    public string HttpHeaderName { get; set; } = "Authorization";

    /// <summary>
    /// Gets or sets the primary API key for authentication.
    /// </summary>
    /// <value>Default: <c>null</c> (must be configured for authorization to work).</value>
    public string? AccessKey1 { get; set; }

    /// <summary>
    /// Gets or sets the secondary API key for authentication.
    /// Enables zero-downtime key rotation by accepting either key.
    /// </summary>
    /// <value>Default: <c>null</c>.</value>
    public string? AccessKey2 { get; set; }
}
