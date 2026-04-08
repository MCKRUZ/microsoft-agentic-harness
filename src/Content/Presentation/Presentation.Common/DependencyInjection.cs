using Domain.Common.Config.Http;
using Infrastructure.APIAccess.Common.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Common;

/// <summary>
/// Dependency injection configuration for the Presentation.Common project.
/// Registers presentation-layer web API services that wrap Infrastructure.APIAccess
/// extension methods into a single composable call.
/// </summary>
/// <remarks>
/// <para>
/// This class acts as the Presentation layer's facade over the APIAccess
/// infrastructure extensions. Each call delegates to a well-tested extension
/// method in <c>Infrastructure.APIAccess.Common.Extensions</c>.
/// </para>
/// <para><b>Registered Services:</b></para>
/// <list type="bullet">
///   <item>Kestrel Server Options — production-ready connection and body-size limits</item>
///   <item>API Versioning — header-based <c>X-Api-Version</c> versioning</item>
///   <item>Swagger/OpenAPI — documentation generation (when enabled in config)</item>
///   <item>Rate Limiter — fixed-window throttling for AI and MCP endpoints</item>
///   <item>CORS — origin-allowlist policies for default, copilot, and MCP scenarios</item>
/// </list>
/// <para>
/// Called from the Presentation composition root (e.g., <c>Program.cs</c>):
/// <code>
/// services.AddPresentationCommonDependencies(appConfig.Http);
/// </code>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Presentation.Common dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="httpConfig">
    /// HTTP configuration containing CORS origins, Swagger settings, and authorization flags.
    /// Sourced from <c>AppConfig.Http</c>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPresentationCommonDependencies(
        this IServiceCollection services,
        HttpConfig httpConfig)
    {
        ArgumentNullException.ThrowIfNull(httpConfig);

        services.AddCustomKestrelServerOptions();
        services.AddCustomApiVersioning();
        services.AddCustomSwaggerGen(httpConfig);
        services.AddCustomRateLimiter();
        services.AddCustomCorsPolicy(httpConfig);

        return services;
    }
}
