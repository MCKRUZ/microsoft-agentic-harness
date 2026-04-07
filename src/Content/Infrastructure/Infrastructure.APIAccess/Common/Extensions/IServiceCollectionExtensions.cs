using Asp.Versioning;
using CorrelationId.HttpClient;
using Domain.Common.Config;
using Domain.Common.Config.Http;
using Domain.Common.Constants;
using Infrastructure.APIAccess.Handlers;
using Infrastructure.APIAccess.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Infrastructure.APIAccess.Common.Extensions;

/// <summary>
/// Extension methods for configuring <see cref="IServiceCollection"/> with HTTP client
/// pipeline components, API server options, and resilience policies.
/// </summary>
public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Exception types considered network-related and eligible for retry.
    /// </summary>
    private static readonly ImmutableArray<Type> NetworkExceptions =
    [
        typeof(SocketException),
        typeof(HttpRequestException),
    ];

    /// <summary>
    /// Exception types related to resilience strategies and eligible for retry.
    /// </summary>
    private static readonly ImmutableArray<Type> StrategyExceptions =
    [
        typeof(TimeoutRejectedException),
        typeof(BrokenCircuitException),
        typeof(RateLimiterRejectedException),
    ];

    /// <summary>
    /// Combined set of all exception types that should be considered retryable.
    /// </summary>
    private static readonly ImmutableArray<Type> RetryableExceptions =
        NetworkExceptions.Union(StrategyExceptions).ToImmutableArray();

    /// <summary>
    /// Configures Kestrel server options with production-ready limits.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Limits applied:
    /// <list type="bullet">
    ///   <item>MaxConcurrentConnections: 100</item>
    ///   <item>MaxConcurrentUpgradedConnections: 100</item>
    ///   <item>MaxRequestBodySize: 10 MB</item>
    ///   <item>RequestHeadersTimeout: 1 minute</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddCustomKestrelServerOptions(this IServiceCollection services)
    {
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxConcurrentConnections = 100;
            options.Limits.MaxConcurrentUpgradedConnections = 100;
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
        });

        return services;
    }

    /// <summary>
    /// Configures API versioning with header-based version detection using <c>X-Api-Version</c>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Default version is 1.0. Unspecified versions assume the default.
    /// </remarks>
    public static IServiceCollection AddCustomApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.ReportApiVersions = true;
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ApiVersionReader = new HeaderApiVersionReader("X-Api-Version");
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'V";
            options.SubstituteApiVersionInUrl = true;
        });

        return services;
    }

    /// <summary>
    /// Configures Swagger/OpenAPI generation with XML documentation and optional security scheme.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">Application configuration containing Swagger settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Only configures Swagger when <c>OpenApiEnabled</c> is true in configuration.
    /// Includes XML documentation comments and security scheme when authorization is not service-managed.
    /// </remarks>
    public static IServiceCollection AddCustomSwaggerGen(this IServiceCollection services, AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        if (!appConfig.Http.HttpSwagger.OpenApiEnabled)
            return services;

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(c =>
        {
            var spec = appConfig.Http.HttpSwagger.OpenApiSpec;
            var info = spec.HttpOpenApiInfo;

            c.SwaggerDoc(spec.SpecName, new OpenApiInfo
            {
                Title = info.Title,
                Version = info.Version,
                Description = info.Description,
                TermsOfService = info.TermsOfService,
                Contact = new OpenApiContact
                {
                    Name = info.HttpOpenApiContact.Name,
                    Email = info.HttpOpenApiContact.Email,
                },
                License = new OpenApiLicense
                {
                    Name = info.HttpOpenApiLicense.Name,
                    Url = info.HttpOpenApiLicense.Url,
                },
            });

            var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlFilePath = Path.Combine(AppContext.BaseDirectory, xmlFilename);

            if (File.Exists(xmlFilePath))
                c.IncludeXmlComments(xmlFilePath);

            if (appConfig.Http.HttpSwagger.ServiceAuthorizationEnabled)
                return;

            var securityScheme = info.HttpOpenApiSecurityScheme;

            var scheme = new OpenApiSecurityScheme
            {
                Description = securityScheme.Description,
                Type = Enum.Parse<SecuritySchemeType>(securityScheme.Type, ignoreCase: true),
                Scheme = securityScheme.Scheme,
                Name = securityScheme.Name,
                In = Enum.Parse<ParameterLocation>(securityScheme.In, ignoreCase: true),
            };

            c.AddSecurityDefinition(securityScheme.Name, scheme);
        });

        return services;
    }

    /// <summary>
    /// Configures fixed-window rate limiting policies for AI and MCP endpoints.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registers two policies, both with 100 requests/minute and a 10-slot queue:
    /// <list type="bullet">
    ///   <item><see cref="PolicyNameConstants.RATE_LIMITER_AI_DEFAULT_POLICY"/></item>
    ///   <item><see cref="PolicyNameConstants.RATE_LIMITER_AI_MCPSERVER_POLICY"/></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddCustomRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter(PolicyNameConstants.RATE_LIMITER_AI_DEFAULT_POLICY, limiterOptions =>
            {
                limiterOptions.PermitLimit = 100;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 10;
            });

            options.AddFixedWindowLimiter(PolicyNameConstants.RATE_LIMITER_AI_MCPSERVER_POLICY, limiterOptions =>
            {
                limiterOptions.PermitLimit = 100;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 10;
            });
        });

        return services;
    }

    /// <summary>
    /// Configures CORS policies for default, config-driven, AI Copilot, and MCP Server scenarios.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">Application configuration containing allowed origins.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCustomCorsPolicy(this IServiceCollection services, AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        var origins = appConfig.Http.CorsAllowedOrigins
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin();
                policy.AllowAnyHeader();
            });

            options.AddPolicy(PolicyNameConstants.CORS_CONFIG_POLICY, policy =>
            {
                policy
                    .WithOrigins(origins)
                    .WithHeaders("*");
            });

            options.AddPolicy(PolicyNameConstants.CORS_AI_COPILOT_POLICY, policy =>
            {
                policy
                    .WithMethods("HEAD", "GET", "POST", "PUT", "DELETE")
                    .WithExposedHeaders("Content-Type", "Content-Length", "Last-Modified");
            });

            options.AddPolicy(PolicyNameConstants.CORS_AI_MCPSERVER_POLICY, policy =>
            {
                policy
                    .WithMethods("GET", "POST")
                    .WithHeaders("Authorization", "Content-Type")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(5));
            });
        });

        return services;
    }

    /// <summary>
    /// Configures Polly resilience pipelines with retry, timeout, circuit breaker, and rate limiting.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configurationName">The name of the resilience pipeline configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Strategies applied in order:
    /// <list type="bullet">
    ///   <item>Retry - Exponential backoff with jitter for retryable exceptions</item>
    ///   <item>Timeout - Prevents operations from running too long</item>
    ///   <item>Circuit Breaker - Stops calling failing services after a threshold</item>
    ///   <item>Rate Limiter - Sliding window rate limiting (100 requests/minute)</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddResiliencePipelines(
        this IServiceCollection services,
        string configurationName)
    {
        services.AddResiliencePipeline(configurationName, (builder, context) =>
        {
            var serviceProvider = context.ServiceProvider;
            var appConfig = serviceProvider.GetService<IOptionsMonitor<AppConfig>>()!.CurrentValue;

            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = ex => new ValueTask<bool>(RetryableExceptions.Contains(ex.GetType())),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = appConfig.Http.Policies.HttpRetry.Count,
                Delay = appConfig.Http.Policies.HttpRetry.Delay,
            });

            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = appConfig.Http.Policies.HttpTimeout.Timeout,
            });

            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = appConfig.Http.Policies.HttpCircuitBreaker.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 100,
                BreakDuration = appConfig.Http.Policies.HttpCircuitBreaker.DurationOfBreak,
            });

            builder.AddRateLimiter(new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    SegmentsPerWindow = 4,
                    Window = TimeSpan.FromMinutes(1),
                }));

            // TODO: Register telemetry enrichers when HttpClientCustomMeteringEnricher is ported.
            // var telemetryOptions = new TelemetryOptions();
            // telemetryOptions.MeteringEnrichers.Add(new HttpClientCustomMeteringEnricher());
            // builder.ConfigureTelemetry(telemetryOptions);
        });

        // TODO: AddResilienceEnricher() — requires Microsoft.Extensions.Http.Resilience enricher registration.
        // services.AddResilienceEnricher();

        return services;
    }

    /// <summary>
    /// Adds the default HTTP client configuration with standard delegating handlers
    /// for correlation, logging, and User-Agent propagation.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Configures <c>HttpClientDefaults</c> so every HTTP client created via
    /// <c>IHttpClientFactory</c> inherits these handlers and the default timeout.
    /// </remarks>
    public static IServiceCollection AddDefaultHttpClient(this IServiceCollection services)
    {
        services
            .AddTransient<CorrelationIdDelegatingHandler>()
            .AddTransient<UserAgentDelegatingHandler>()
            .AddTransient<LoggingDelegatingHandler>()
            .ConfigureHttpClientDefaults(httpClientBuilder =>
            {
                httpClientBuilder.ConfigureHttpClient((serviceProvider, httpClient) =>
                {
                    var appConfig = serviceProvider.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
                    httpClient.Timeout = appConfig.Http.Policies.HttpTimeout.Timeout;
                });

                httpClientBuilder.ConfigurePrimaryHttpMessageHandler(_ => new DefaultHttpClientHandler());

                httpClientBuilder.AddCorrelationIdForwarding();
                httpClientBuilder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
                httpClientBuilder.AddHttpMessageHandler<LoggingDelegatingHandler>();
                httpClientBuilder.AddHttpMessageHandler<UserAgentDelegatingHandler>();
            });

        services.AddResiliencePipelines("");

        return services;
    }

    /// <summary>
    /// Adds a typed HTTP client with configuration from <see cref="AppConfig"/>
    /// using <see cref="ApiEndpointResolverService"/> for endpoint resolution.
    /// </summary>
    /// <typeparam name="TClient">The HTTP client interface type.</typeparam>
    /// <typeparam name="TImplementation">The HTTP client implementation type.</typeparam>
    /// <typeparam name="TClientOptions">The client configuration type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configurationSectionName">
    /// The configuration path for the client settings in <see cref="AppConfig"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Configures BaseAddress and Timeout from AppConfig, adds standard delegating
    /// handlers, and applies resilience policies for retry, timeout, circuit breaker,
    /// and rate limiting.
    /// </remarks>
    public static IServiceCollection AddHttpClient<TClient, TImplementation, TClientOptions>(
        this IServiceCollection services,
        string configurationSectionName)
        where TClient : class
        where TImplementation : class, TClient
        where TClientOptions : HttpClientConfig, new()
    {
        services
            .AddTransient<CorrelationIdDelegatingHandler>()
            .AddTransient<UserAgentDelegatingHandler>()
            .AddTransient<LoggingDelegatingHandler>()
            .AddHttpClient<TClient, TImplementation>()
            .ConfigureHttpClient((serviceProvider, httpClient) =>
            {
                var resolver = serviceProvider.GetRequiredService<ApiEndpointResolverService>();
                var clientOptions = resolver.GetClientConfig<TClientOptions>(configurationSectionName);
                httpClient.BaseAddress = new Uri(clientOptions.BaseAddress);
                httpClient.Timeout = clientOptions.Timeout;
            })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var resolver = serviceProvider.GetRequiredService<ApiEndpointResolverService>();
                var clientOptions = resolver.GetClientConfig<TClientOptions>(configurationSectionName);
                return new DefaultHttpClientHandler(clientOptions);
            })
            .AddCorrelationIdForwarding()
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddHttpMessageHandler<LoggingDelegatingHandler>()
            .AddHttpMessageHandler<UserAgentDelegatingHandler>();

        services.AddResiliencePipelines(configurationSectionName);

        return services;
    }
}
