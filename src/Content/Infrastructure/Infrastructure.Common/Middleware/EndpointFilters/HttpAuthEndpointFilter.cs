using Domain.Common.Config.Http;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Common.Middleware.EndpointFilters;

/// <summary>
/// Endpoint filter that validates incoming requests against configured API keys.
/// Supports dual-key authentication for zero-downtime key rotation.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="HttpAuthorizationConfig.Enabled"/> is <c>true</c>, the filter reads
/// the API key from the header specified by <see cref="HttpAuthorizationConfig.HttpHeaderName"/>
/// and validates it against <see cref="HttpAuthorizationConfig.AccessKey1"/> and
/// <see cref="HttpAuthorizationConfig.AccessKey2"/>. Either key is accepted.
/// </para>
/// <para>
/// Apply to minimal API endpoints or groups:
/// <code>
/// app.MapGet("/api/data", handler).AddEndpointFilter&lt;HttpAuthEndpointFilter&gt;();
/// </code>
/// </para>
/// <para>
/// <strong>Security:</strong> Store API keys in User Secrets (development) or
/// Azure Key Vault (production). Keys are compared using ordinal (case-sensitive)
/// string comparison to prevent timing-based inference attacks via case folding.
/// </para>
/// </remarks>
public sealed class HttpAuthEndpointFilter : IEndpointFilter
{
    private readonly HttpAuthorizationConfig _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpAuthEndpointFilter"/> class.
    /// </summary>
    /// <param name="config">The API key authorization configuration.</param>
    public HttpAuthEndpointFilter(HttpAuthorizationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// Validates the API key from the configured HTTP header and either continues
    /// the pipeline or returns an appropriate error response.
    /// </summary>
    /// <param name="context">The endpoint filter invocation context.</param>
    /// <param name="next">The next filter delegate in the pipeline.</param>
    /// <returns>
    /// The result from the next filter when authorization succeeds, or a Problem Details
    /// result with 401 (missing key) or 403 (invalid key) status.
    /// </returns>
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!_config.Enabled)
        {
            return await next(context);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(_config.HttpHeaderName, out var apiKey))
        {
            return Results.Problem(detail: "Missing API Key HTTP header", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!string.Equals(apiKey, _config.AccessKey1, StringComparison.Ordinal)
            && !string.Equals(apiKey, _config.AccessKey2, StringComparison.Ordinal))
        {
            return Results.Problem(detail: "Invalid API Key", statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
