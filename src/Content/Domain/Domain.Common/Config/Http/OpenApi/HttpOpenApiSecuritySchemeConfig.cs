namespace Domain.Common.Config.Http.OpenApi;

/// <summary>
/// Configuration POCO for OpenAPI security scheme settings.
/// String values for <see cref="Type"/> and <see cref="In"/> are parsed
/// to their respective OpenAPI enum types in Infrastructure.APIAccess.
/// </summary>
/// <remarks>
/// Example appsettings.json:
/// <code>
/// "HttpOpenApiSecurityScheme": {
///   "Name": "Authorization",
///   "Type": "Http",
///   "Scheme": "bearer",
///   "In": "Header",
///   "Description": "JWT Bearer token"
/// }
/// </code>
/// </remarks>
public class HttpOpenApiSecuritySchemeConfig
{
    /// <summary>
    /// Gets or sets the security scheme description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the security scheme type as a string.
    /// Valid values: "ApiKey", "Http", "OAuth2", "OpenIdConnect".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the authentication scheme name (e.g., "bearer").
    /// </summary>
    public string Scheme { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the security scheme name (e.g., "Authorization").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the location of the security parameter as a string.
    /// Valid values: "Query", "Header", "Path", "Cookie".
    /// </summary>
    public string In { get; set; } = string.Empty;
}
