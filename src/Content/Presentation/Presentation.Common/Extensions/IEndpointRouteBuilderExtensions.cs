using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Presentation.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="IEndpointRouteBuilder"/> that register
/// health check and diagnostics endpoints.
/// </summary>
public static class IEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the health checks UI endpoint under the specified API prefix
    /// with an optional authentication filter.
    /// </summary>
    /// <param name="builder">The endpoint route builder.</param>
    /// <param name="apiPrefix">
    /// The route prefix for the health checks UI endpoint.
    /// Default: <c>"/"</c>.
    /// </param>
    /// <param name="authFilter">
    /// Optional <see cref="IEndpointFilter"/> to apply authentication/authorization
    /// to the health checks UI endpoint. When <c>null</c>, the endpoint is unprotected.
    /// </param>
    /// <returns>The endpoint route builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method requires the <c>AspNetCore.HealthChecks.UI</c> package and
    /// a prior call to <c>AddHealthChecksUI()</c> in DI registration.
    /// </para>
    /// <para>
    /// Usage in <c>Program.cs</c>:
    /// <code>
    /// app.AddHealthCheckEndpoint("/api");
    /// // or with auth:
    /// app.AddHealthCheckEndpoint("/api", new MyAuthFilter());
    /// </code>
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder AddHealthCheckEndpoint(
        this IEndpointRouteBuilder builder,
        string apiPrefix = "/",
        IEndpointFilter? authFilter = null)
    {
        var group = builder.MapGroup(apiPrefix);

        var healthCheckEndpoint = group.MapHealthChecksUI();

        if (authFilter is not null)
            healthCheckEndpoint.AddEndpointFilter(authFilter);

        return builder;
    }
}
