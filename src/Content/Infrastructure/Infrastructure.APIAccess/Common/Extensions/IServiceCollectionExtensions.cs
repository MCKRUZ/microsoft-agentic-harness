using Asp.Versioning;
using CorrelationId.HttpClient;
using Domain.Common.Config.Http;
using Domain.Common.Config.Http.Policies;
using Domain.Common.Constants;
using Infrastructure.APIAccess.Handlers;
using Infrastructure.APIAccess.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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
    /// Resilience-strategy exception types eligible for retry. Only the per-attempt timeout
    /// qualifies: it sits INSIDE the retry strategy, so retrying a timed-out attempt is the
    /// point of having it. <c>BrokenCircuitException</c> and <c>RateLimiterRejectedException</c>
    /// are deliberately NOT retryable — they are raised by strategies inside this same
    /// pipeline, so retrying them only burns exponential backoff on self-inflicted rejections;
    /// callers should fast-fail and let the circuit/limiter recover.
    /// </summary>
    private static readonly ImmutableArray<Type> StrategyExceptions =
    [
        typeof(TimeoutRejectedException),
    ];

    /// <summary>
    /// Combined set of all exception types that should be considered retryable.
    /// </summary>
    private static readonly ImmutableArray<Type> RetryableExceptions =
        NetworkExceptions.Union(StrategyExceptions).ToImmutableArray();

    /// <summary>
    /// HTTP methods that are idempotent by contract and therefore safe to retry
    /// automatically. Kept deliberately conservative (GET/HEAD/OPTIONS): although RFC 9110
    /// also declares PUT and DELETE idempotent, harness consumers routinely front
    /// non-idempotent handlers with them, so those are excluded too.
    /// </summary>
    private static readonly ImmutableArray<HttpMethod> IdempotentHttpMethods =
    [
        HttpMethod.Get,
        HttpMethod.Head,
        HttpMethod.Options,
    ];

    /// <summary>
    /// Decides whether a failed HTTP attempt may be retried automatically: the failure must be
    /// a declared transient exception type AND the request method must be idempotent
    /// (GET/HEAD/OPTIONS). Non-idempotent requests (POST, PATCH, ...) are never retried — a
    /// "transient" network failure can surface AFTER the server processed the request
    /// (connection dropped on the response path), so retrying would duplicate side effects
    /// (A2A messages, GitOps syncs, connector mutations, webhooks, label operations).
    /// </summary>
    /// <param name="exception">The exception the attempt failed with, if any.</param>
    /// <param name="requestMethod">The HTTP method of the request being executed, if known.</param>
    /// <returns><see langword="true"/> when the attempt may be retried.</returns>
    public static bool IsRetryableHttpFailure(Exception? exception, HttpMethod? requestMethod)
        => exception is not null
           && RetryableExceptions.Contains(exception.GetType())
           && requestMethod is not null
           && IdempotentHttpMethods.Contains(requestMethod);

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
    public static IServiceCollection AddCustomSwaggerGen(this IServiceCollection services, HttpConfig httpConfig)
    {
        ArgumentNullException.ThrowIfNull(httpConfig);

        if (!httpConfig.HttpSwagger.OpenApiEnabled)
            return services;

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(c =>
        {
            var spec = httpConfig.HttpSwagger.OpenApiSpec;
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

            if (httpConfig.HttpSwagger.ServiceAuthorizationEnabled)
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
    public static IServiceCollection AddCustomCorsPolicy(this IServiceCollection services, HttpConfig httpConfig)
    {
        ArgumentNullException.ThrowIfNull(httpConfig);

        var origins = httpConfig.CorsAllowedOrigins
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy
                    .WithOrigins(origins)
                    .AllowAnyHeader();
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
    /// Attaches the harness Polly resilience pipeline (total timeout, retry, per-attempt
    /// timeout, circuit breaker, rate limiter) to an HTTP client so every request the client
    /// sends executes through it. The underlying <c>Microsoft.Extensions.Http.Resilience</c>
    /// handler snapshots the request message before each attempt, which makes a request
    /// RE-SENDABLE — it does not make resending SAFE. Retries are therefore restricted to
    /// idempotent methods (GET/HEAD/OPTIONS); POST and other non-idempotent requests are
    /// deliberately never retried, because a transient failure can surface after the server
    /// already processed the request and a retry would duplicate its side effects.
    /// </summary>
    /// <param name="httpClientBuilder">The HTTP client builder to attach the handler to.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// Strategies applied in order (outermost to innermost):
    /// <list type="bullet">
    ///   <item>Total Timeout - Bounds the whole operation across all attempts (see
    ///   <c>HttpTimeoutPolicyConfig.TotalTimeout</c>; computed from the retry budget when unset)</item>
    ///   <item>Retry - Exponential backoff with jitter, idempotent methods only</item>
    ///   <item>Per-Attempt Timeout - Cancels a single slow attempt so the next one can start</item>
    ///   <item>Circuit Breaker - Stops calling failing services after a threshold</item>
    ///   <item>Rate Limiter - Sliding window rate limiting (100 requests/minute)</item>
    /// </list>
    /// All tuning values come from <c>AppConfig:Http:Policies</c> (<see cref="HttpPolicyConfig"/>).
    /// <see cref="AddDefaultHttpClient"/> applies this handler to every client created via
    /// <c>IHttpClientFactory</c> and sets <c>HttpClient.Timeout</c> to infinite so the outer
    /// client timeout cannot race the pipeline and truncate the retry budget; do not attach the
    /// handler a second time to individual clients or retries will compound multiplicatively.
    /// </remarks>
    public static IHttpClientBuilder AddCustomResilienceHandler(this IHttpClientBuilder httpClientBuilder)
    {
        httpClientBuilder.AddResilienceHandler("harness-http", (builder, context) =>
        {
            var httpConfig = context.ServiceProvider
                .GetRequiredService<IOptionsMonitor<HttpConfig>>().CurrentValue;

            // Total timeout (outermost): bounds attempts + backoff. HttpClient.Timeout is
            // infinite, so this strategy owns the overall budget.
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = ResolveTotalTimeout(httpConfig.Policies),
            });

            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                // Retry only when the ATTEMPT'S EXCEPTION is a declared retryable type AND the
                // request method is idempotent. The predicate receives Polly's
                // predicate-arguments struct — inspecting the struct itself (a former bug:
                // RetryableExceptions.Contains(args.GetType())) can never match, silently
                // disabling all retries.
                ShouldHandle = args => new ValueTask<bool>(IsRetryableHttpFailure(
                    args.Outcome.Exception, args.Context.GetRequestMessage()?.Method)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = httpConfig.Policies.HttpRetry.Count,
                Delay = httpConfig.Policies.HttpRetry.Delay,
            });

            // Per-attempt timeout (inside retry): cancels one slow attempt; the retry strategy
            // decides whether another attempt is allowed.
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = httpConfig.Policies.HttpTimeout.Timeout,
            });

            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = httpConfig.Policies.HttpCircuitBreaker.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 100,
                BreakDuration = httpConfig.Policies.HttpCircuitBreaker.DurationOfBreak,
            });

            builder.AddRateLimiter(new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    SegmentsPerWindow = 4,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        return httpClientBuilder;
    }

    /// <summary>
    /// Resolves the total-operation timeout for the resilience pipeline: the configured
    /// <c>HttpTimeoutPolicyConfig.TotalTimeout</c> when set, otherwise
    /// <c>(HttpRetry.Count + 1) × per-attempt timeout</c> plus exponential-backoff headroom
    /// (<c>Delay × 2^Count</c>), capped at 24 hours (Polly's strategy maximum).
    /// </summary>
    private static TimeSpan ResolveTotalTimeout(HttpPolicyConfig policies)
    {
        if (policies.HttpTimeout.TotalTimeout is { } configured)
            return configured;

        var attempts = policies.HttpRetry.Count + 1;
        var backoffFactor = 1L << Math.Min(policies.HttpRetry.Count, 10);
        var computed = TimeSpan.FromTicks(policies.HttpTimeout.Timeout.Ticks * attempts)
            + TimeSpan.FromTicks(policies.HttpRetry.Delay.Ticks * backoffFactor);

        var max = TimeSpan.FromHours(24);
        return computed < max ? computed : max;
    }

    /// <summary>
    /// Adds the default HTTP client configuration with standard delegating handlers
    /// for correlation, logging, and User-Agent propagation.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Configures <c>HttpClientDefaults</c> so every HTTP client created via
    /// <c>IHttpClientFactory</c> inherits these handlers, the default timeout, and the harness
    /// resilience pipeline (see <see cref="AddCustomResilienceHandler"/>).
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
                    // The resilience pipeline owns BOTH the per-attempt timeout and the total
                    // timeout (see AddCustomResilienceHandler). HttpClient.Timeout must not
                    // race it: when both are 30s the outer timeout cancels the whole operation
                    // on the first slow attempt, making timeout-triggered retries dead code.
                    httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                });

                httpClientBuilder.ConfigurePrimaryHttpMessageHandler(_ => new DefaultHttpClientHandler());

                httpClientBuilder.AddCorrelationIdForwarding();
                httpClientBuilder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
                httpClientBuilder.AddHttpMessageHandler<LoggingDelegatingHandler>();
                httpClientBuilder.AddHttpMessageHandler<UserAgentDelegatingHandler>();

                // Attach the resilience pipeline to EVERY factory-created client. Registering
                // the pipeline in the Polly registry alone (the previous approach) leaves it
                // inert — nothing executes a registry pipeline unless a handler runs it.
                httpClientBuilder.AddCustomResilienceHandler();
            });

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
    /// Configures BaseAddress and Timeout from AppConfig and adds standard delegating handlers.
    /// The resilience pipeline (retry, timeout, circuit breaker, rate limiting) is inherited
    /// from the client defaults registered by <see cref="AddDefaultHttpClient"/> — it is not
    /// attached here a second time, because duplicate handlers compound retries
    /// multiplicatively. Hosts must call <see cref="AddDefaultHttpClient"/> (done by
    /// <c>AddInfrastructureApiAccessDependencies</c>) for typed clients to be resilient.
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

        return services;
    }
}
