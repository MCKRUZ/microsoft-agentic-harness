using Domain.Common.Config.Http.OpenApi;

namespace Domain.Common.Config.Http;

/// <summary>
/// Configuration for Swagger/OpenAPI generation including spec details
/// and authorization settings.
/// </summary>
/// <remarks>
/// Binds to the <c>AppConfig:Http:HttpSwagger</c> section in appsettings.json.
/// </remarks>
public class HttpSwaggerConfig
{
    /// <summary>
    /// Gets or sets whether OpenAPI document generation is enabled.
    /// </summary>
    public bool OpenApiEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether service-level authorization is enabled for Swagger.
    /// When true, the security scheme definition is skipped.
    /// </summary>
    public bool ServiceAuthorizationEnabled { get; set; }

    /// <summary>
    /// Gets or sets the HTTP header name used for authorization in Swagger.
    /// </summary>
    public string HttpHeaderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenAPI specification configuration.
    /// </summary>
    public HttpOpenApiSpecConfig OpenApiSpec { get; set; } = new();
}
