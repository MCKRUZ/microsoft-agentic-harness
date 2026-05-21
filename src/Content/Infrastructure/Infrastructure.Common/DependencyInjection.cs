using System.Data.Common;
using Application.Common.Interfaces.Data;
using Application.Common.Interfaces.Security;
using Domain.Common.Config.Http;
using Infrastructure.Common.Data;
using Infrastructure.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Common;

/// <summary>
/// Dependency injection configuration for the Infrastructure.Common layer.
/// Registers cross-cutting infrastructure services: identity, HTTP authorization config, etc.
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

        // HTTP authorization config — resolved from HttpConfig for endpoint filters
        services.AddSingleton(sp =>
        {
            var httpConfig = sp.GetRequiredService<IOptionsMonitor<HttpConfig>>();
            return httpConfig.CurrentValue.Authorization;
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="ISqlConnectionFactory"/> with the specified <see cref="DbProviderFactory"/>.
    /// Call this overload when the SQL database retrieval source is enabled.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="providerFactory">
    /// The database-vendor-specific provider factory (e.g. <c>SqlClientFactory.Instance</c>,
    /// <c>NpgsqlFactory.Instance</c>, <c>SqliteFactory.Instance</c>).
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlConnectionFactory(
        this IServiceCollection services,
        DbProviderFactory providerFactory)
    {
        services.AddSingleton(providerFactory);
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        return services;
    }
}
