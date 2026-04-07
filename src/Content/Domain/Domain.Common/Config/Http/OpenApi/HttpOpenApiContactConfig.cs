namespace Domain.Common.Config.Http.OpenApi;

/// <summary>
/// Configuration POCO for OpenAPI contact information.
/// </summary>
public class HttpOpenApiContactConfig
{
    /// <summary>
    /// Gets or sets the contact name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the contact email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;
}
