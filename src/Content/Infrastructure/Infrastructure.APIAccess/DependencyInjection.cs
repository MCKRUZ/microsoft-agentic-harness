using CorrelationId.DependencyInjection;
using Infrastructure.APIAccess.Auth.Handlers;
using Infrastructure.APIAccess.Auth.Providers;
using Infrastructure.APIAccess.Common.Extensions;
using Infrastructure.APIAccess.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.APIAccess;

/// <summary>
/// Dependency injection configuration for the Infrastructure.APIAccess project.
/// Registers correlation ID support, memory cache, endpoint resolver,
/// and the default HTTP client pipeline.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds all Infrastructure.APIAccess dependencies to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registers the following:
    /// <list type="bullet">
    ///   <item>Correlation ID functionality for request tracing</item>
    ///   <item>Memory cache for endpoint resolution caching</item>
    ///   <item>ApiEndpointResolverService for typed client configuration</item>
    ///   <item>Default HTTP client with standard delegating handlers</item>
    ///   <item>Permission-based authorization (policy provider + handler)</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddInfrastructureApiAccessDependencies(
        this IServiceCollection services)
    {
        services
            .AddDefaultCorrelationId(options =>
            {
                options.AddToLoggingScope = true;
            })
            .AddMemoryCache()
            .AddSingleton<ApiEndpointResolverService>()
            .AddDefaultHttpClient();

        // Permission-based authorization
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthHandler>();

        return services;
    }
}
