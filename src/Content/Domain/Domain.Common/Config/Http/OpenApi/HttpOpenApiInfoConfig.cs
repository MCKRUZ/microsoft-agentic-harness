namespace Domain.Common.Config.Http.OpenApi;

/// <summary>
/// Pure configuration POCO for OpenAPI document info section.
/// Maps to appsettings.json without any framework dependencies.
/// </summary>
/// <remarks>
/// The mapping from these POCOs to actual <c>Microsoft.OpenApi.Models</c> types
/// happens in Infrastructure.APIAccess where Swagger is configured.
/// This keeps Domain free of OpenAPI framework dependencies.
/// </remarks>
public class HttpOpenApiInfoConfig
{
    /// <summary>
    /// Gets or sets the API title displayed in the Swagger UI.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API version string (e.g., "v1", "1.0.0").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API description shown in the Swagger UI.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL to the API's terms of service.
    /// </summary>
    public Uri? TermsOfService { get; set; }

    /// <summary>
    /// Gets or sets the API contact information.
    /// </summary>
    public HttpOpenApiContactConfig HttpOpenApiContact { get; set; } = new();

    /// <summary>
    /// Gets or sets the API license information.
    /// </summary>
    public HttpOpenApiLicenseConfig HttpOpenApiLicense { get; set; } = new();

    /// <summary>
    /// Gets or sets the API security scheme configuration.
    /// </summary>
    public HttpOpenApiSecuritySchemeConfig HttpOpenApiSecurityScheme { get; set; } = new();
}
