using Domain.Common.Config.Http;
using Infrastructure.Common.Middleware.EndpointFilters;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.APIAccess.Common.Helpers;

/// <summary>
/// Factory for creating pre-configured sets of endpoint filters
/// for minimal API route registration.
/// </summary>
/// <remarks>
/// Centralizes filter composition so Presentation-layer endpoint definitions
/// don't need to know about individual filter types or their construction order.
/// </remarks>
public static class EndpointFilterHelper
{
    /// <summary>
    /// Creates the standard authentication filter pipeline: error handling + API key validation.
    /// </summary>
    /// <param name="config">
    /// The authorization configuration. When <c>null</c>, a default (disabled) configuration is used.
    /// </param>
    /// <returns>
    /// An ordered array of endpoint filters: <see cref="HttpErrorEndpointFilter"/> first
    /// (catches exceptions), then <see cref="HttpAuthEndpointFilter"/> (validates API keys).
    /// </returns>
    /// <example>
    /// <code>
    /// var filters = EndpointFilterHelper.GetAuthEndpointFilters(appConfig.Http.Authorization);
    /// app.MapGet("/api/data", handler).AddFilters(filters);
    /// </code>
    /// </example>
    public static IEndpointFilter[] GetAuthEndpointFilters(HttpAuthorizationConfig? config = null)
    {
        config ??= new HttpAuthorizationConfig();

        var errorFilter = new HttpErrorEndpointFilter();
        var authFilter = new HttpAuthEndpointFilter(config);
        return [errorFilter, authFilter];
    }
}
