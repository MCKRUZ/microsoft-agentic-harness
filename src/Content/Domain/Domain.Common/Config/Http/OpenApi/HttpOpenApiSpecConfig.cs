namespace Domain.Common.Config.Http.OpenApi;

/// <summary>
/// Configuration for an OpenAPI specification document including
/// its name and detailed info section.
/// </summary>
public class HttpOpenApiSpecConfig
{
    /// <summary>
    /// Gets or sets the specification document name (used as the SwaggerDoc key).
    /// </summary>
    public string SpecName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the detailed OpenAPI info configuration.
    /// </summary>
    public HttpOpenApiInfoConfig HttpOpenApiInfo { get; set; } = new();
}
