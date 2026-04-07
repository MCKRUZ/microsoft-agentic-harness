using Domain.Common.Config.Http.Policies;

namespace Domain.Common.Config.Http;

/// <summary>
/// Configuration for HTTP-related settings including CORS, authorization,
/// Swagger/OpenAPI, and resilience policies.
/// </summary>
/// <remarks>
/// Binds to <c>AppConfig:Http</c> in appsettings.json. Consumed by middleware
/// via <c>IOptionsMonitor&lt;AppConfig&gt;</c> for runtime configuration changes.
/// <para>
/// Example configuration:
/// <code>
/// "AppConfig": {
///   "Http": {
///     "CorsAllowedOrigins": "https://localhost:4200;https://app.example.com",
///     "Authorization": {
///       "Enabled": true,
///       "HttpHeaderName": "X-API-Key",
///       "AccessKey1": "key-from-user-secrets"
///     },
///     "HttpSwagger": { "OpenApiEnabled": true },
///     "Policies": { "HttpRetry": { "Count": 3 } }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
// Mutable setters required by IOptionsMonitor<T> binding. Treat as read-only after DI setup.
public class HttpConfig
{
    /// <summary>
    /// Gets or sets the semicolon-separated list of allowed CORS origins.
    /// </summary>
    /// <value>
    /// Default: empty string (no origins allowed).
    /// Example: <c>"https://localhost:4200;https://app.example.com"</c>
    /// </value>
    public string CorsAllowedOrigins { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the retry interval in milliseconds for 503 maintenance responses.
    /// </summary>
    /// <value>Default: 5300 milliseconds.</value>
    public int MaintenanceCode503RetryInterval { get; set; } = 5300;

    /// <summary>
    /// Gets or sets the API key authorization configuration for HTTP endpoints.
    /// </summary>
    public HttpAuthorizationConfig Authorization { get; set; } = new();

    /// <summary>
    /// Gets or sets the Swagger/OpenAPI configuration.
    /// </summary>
    public HttpSwaggerConfig HttpSwagger { get; set; } = new();

    /// <summary>
    /// Gets or sets the resilience policy configuration for HTTP clients.
    /// </summary>
    public HttpPolicyConfig Policies { get; set; } = new();
}
