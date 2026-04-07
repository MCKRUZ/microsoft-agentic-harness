using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.APIAccess.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="IEndpointConventionBuilder"/> to simplify
/// endpoint filter registration for minimal APIs.
/// </summary>
public static class IEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Adds multiple endpoint filters to a route in a single operation.
    /// </summary>
    /// <param name="route">The endpoint convention builder to add filters to.</param>
    /// <param name="filters">
    /// An optional array of endpoint filters to add. If <c>null</c> or empty, no action is taken.
    /// </param>
    /// <remarks>
    /// Filters are applied in array order, enabling consistent cross-cutting concern pipelines:
    /// <code>
    /// var filters = EndpointFilterHelper.GetAuthEndpointFilters(config);
    /// app.MapGet("/api/data", handler).AddFilters(filters);
    /// </code>
    /// </remarks>
    public static void AddFilters(this IEndpointConventionBuilder route, IEndpointFilter[]? filters = null)
    {
        if (filters is null || filters.Length == 0)
            return;

        foreach (var filter in filters)
            route.AddEndpointFilter(filter);
    }
}
