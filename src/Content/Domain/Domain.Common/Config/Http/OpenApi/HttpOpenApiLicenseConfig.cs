namespace Domain.Common.Config.Http.OpenApi;

/// <summary>
/// Configuration POCO for OpenAPI license information.
/// </summary>
public class HttpOpenApiLicenseConfig
{
    /// <summary>
    /// Gets or sets the license name (e.g., "MIT", "Apache 2.0").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL to the full license text.
    /// </summary>
    public Uri? Url { get; set; }
}
