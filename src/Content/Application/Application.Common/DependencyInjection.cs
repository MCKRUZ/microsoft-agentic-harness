using Application.Common.Extensions;
using Application.Common.Helpers;
using Application.Common.MediatRBehaviors;
using Domain.Common.Config;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Application.Common;

/// <summary>
/// Dependency injection configuration for the Application.Common layer.
/// Registers cross-cutting concerns: MediatR pipeline, validation, caching, and logging.
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// </code>
/// </para>
/// <para>
/// <strong>MediatR Pipeline Behavior Order (outermost → innermost):</strong>
/// <list type="number">
///   <item><description><c>UnhandledExceptionBehavior</c> — safety net, logs + rethrows</description></item>
///   <item><description><c>RequestValidationBehavior</c> — FluentValidation, returns Result failure</description></item>
///   <item><description><c>AuthorizationBehavior</c> — checks [Authorize] attributes</description></item>
///   <item><description><c>AgentContextPropagationBehavior</c> — sets scoped agent identity</description></item>
///   <item><description><c>ContentSafetyBehavior</c> — screens IContentScreenable requests</description></item>
///   <item><description><c>ToolPermissionBehavior</c> — checks IToolRequest permissions</description></item>
///   <item><description><c>CachingBehavior</c> — hybrid memory/distributed cache</description></item>
///   <item><description><c>RequestTracingBehavior</c> — OTel spans with duration</description></item>
///   <item><description><c>AuditTrailBehavior</c> — records IAuditable requests</description></item>
///   <item><description><c>TimeoutBehavior</c> — enforces IHasTimeout deadlines</description></item>
/// </list>
/// Registration order matters: first registered = outermost wrapper.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Application.Common dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">Application configuration for logging and cache settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddApplicationCommonDependencies(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // FluentValidation — auto-discover validators in this assembly
        services.AddValidatorsFromAssembly(assembly);

        // MediatR — auto-discover handlers in this assembly
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(assembly));

        // Pipeline behaviors — registration order = execution order (outermost first)
        services
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestValidationBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ContentSafetyBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ToolPermissionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestTracingBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditTrailBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutBehavior<,>));

        // Time abstraction — use TimeProvider.System (or FakeTimeProvider in tests)
        services.AddSingleton(TimeProvider.System);

        // Hybrid cache (memory + distributed backing store)
        services.AddHybridCache(cfg =>
            cfg.DefaultEntryOptions = CacheOptionsHelper.GetHybridCacheOptions());

        // Logging pipeline (providers configured by AppConfig.Logging)
        services.ConfigureLogging(appConfig);

        return services;
    }
}
