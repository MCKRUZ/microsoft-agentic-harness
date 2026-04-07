using Application.Common.Interfaces.Security;
using Infrastructure.Common.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Common;

/// <summary>
/// Dependency injection configuration for the Infrastructure.Common layer.
/// Registers cross-cutting infrastructure services: identity, file system, etc.
/// </summary>
/// <remarks>
/// Called from the Presentation composition root after Application dependencies:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// services.AddInfrastructureCommonDependencies();
/// services.AddInfrastructureAIDependencies(allowedPaths);
/// </code>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Infrastructure.Common dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureCommonDependencies(
        this IServiceCollection services)
    {
        // Identity — stub implementation, replace with real provider for production
        // Transient: real implementations will read per-request claims from HttpContext
        services.AddTransient<IIdentityService, IdentityService>();

        return services;
    }
}
