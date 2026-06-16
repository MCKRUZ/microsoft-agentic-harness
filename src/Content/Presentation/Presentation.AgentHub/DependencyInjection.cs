using System.Diagnostics;
using System.Threading.RateLimiting;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.Planner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Web;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Auth;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Presentation.AgentHub.Notifications;
using Presentation.AgentHub.Planner;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Telemetry;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub;

/// <summary>
/// Extension methods for registering AgentHub-specific services.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all AgentHub-specific services: Azure AD authentication with
    /// SignalR token extraction, SignalR hub, CORS, rate limiting, and config binding.
    /// Call this after <see cref="Presentation.Common.Extensions.IServiceCollectionExtensions.GetServices"/>.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <param name="environment">The host environment — used to guard the dev auth bypass.</param>
    public static IServiceCollection AddAgentHubServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                // Serialize enums (e.g. SloVerdict) as strings so controller responses match
                // the frontend TS contracts ('Met' | 'AtRisk' | 'Breached'). Without this,
                // System.Text.Json emits numeric values and the dashboard cannot map them.
                options.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        // Surfaces a missing/invalid AI provider configuration via /health/ai. Additive to the
        // health checks registered in Presentation.Common — Degraded (not Unhealthy) because the
        // host still serves the dashboard and telemetry without a live LLM.
        // Tagged "ai" only (NOT "ready"): a missing LLM key is Degraded, not a reason to fail a
        // readiness probe — the host still serves the dashboard, telemetry, and Echo-mode agents.
        services.AddHealthChecks()
            .AddCheck<HealthChecks.AiProviderHealthCheck>(
                "ai_provider",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                tags: ["ai"]);

        var authDisabled = environment.IsDevelopment()
            && configuration.GetValue<bool>("Auth:Disabled");

        if (authDisabled)
        {
            // Dev bypass: auto-authenticates every request as a synthetic "dev user".
            // Double-guarded: only active when IsDevelopment() AND Auth:Disabled=true.
            services.AddAuthentication(DevAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(
                        DevAuthHandler.SchemeName, _ => { });
        }
        else
        {
            services.AddMicrosoftIdentityWebApiAuthentication(configuration);

            // SignalR WebSocket upgrades cannot carry an Authorization header.
            // The client sends the bearer token as the `access_token` query parameter.
            // Chain onto the existing OnMessageReceived delegate (set by Microsoft.Identity.Web)
            // rather than replacing the entire Events object, which would discard other
            // handlers such as OnTokenValidated and OnAuthenticationFailed.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Project security standard (rules/security.md) mandates ClockSkew=Zero so an
                // expired token is rejected at `exp` rather than the framework default 5-minute
                // grace window. Microsoft.Identity.Web validates issuer/audience/lifetime/signing
                // key by default but leaves ClockSkew at its 5-minute default — override it here.
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;

                options.Events ??= new JwtBearerEvents();
                var existingOnMessageReceived = options.Events.OnMessageReceived;
                options.Events.OnMessageReceived = async context =>
                {
                    if (existingOnMessageReceived != null)
                        await existingOnMessageReceived(context);

                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }
                };
            });
        }

        services.AddAuthorization();

        services.AddSingleton<KnowledgeScopeHubFilter>();
        services.AddSingleton<HubRateLimitFilter>();
        services.AddSignalR(options =>
            {
                if (environment.IsDevelopment())
                    options.EnableDetailedErrors = true;

                options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
                options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                // Establish per-invocation knowledge scope (user/tenant) from the authenticated
                // caller — the SignalR-transport equivalent of KnowledgeScopeMiddleware.
                options.AddFilter<KnowledgeScopeHubFilter>();

                // Throttle the expensive agent-turn hub methods per caller. ASP.NET Core's
                // UseRateLimiter middleware is HTTP-request-scoped and cannot partition individual
                // SignalR hub-method invocations (they arrive over an already-established WebSocket),
                // so a hub filter is the only mechanism that actually throttles per-invocation.
                options.AddFilter<HubRateLimitFilter>();
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        services.AddRateLimiter(options =>
        {
            // Global limiter runs before routing resolves — enforces MCP tool invoke
            // rate limit on the path pattern even before the route handler exists.
            // Applied at 10 POST requests/min per IP on /api/mcp/tools/*.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Method == HttpMethods.Post &&
                    context.Request.Path.StartsWithSegments("/api/mcp/tools"))
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"mcp:{ip}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
                }
                return RateLimitPartition.GetNoLimiter("none");
            });

            // NOTE: SignalR agent-turn methods (SendMessage / RetryFromMessage / EditAndResubmit /
            // InvokeToolViaAgent) are NOT throttled here. UseRateLimiter is HTTP-request-scoped and
            // does not partition hub-method invocations over an established WebSocket. Per-invocation
            // throttling lives in HubRateLimitFilter, added to the SignalR filter chain above.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddCors(options =>
        {
            options.AddPolicy("AgentHubCors", policy =>
            {
                var allowedOrigins = configuration
                    .GetSection("AppConfig:AgentHub:Cors:AllowedOrigins")
                    .Get<string[]>() ?? [];

                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader();
                // AllowCredentials() is intentionally omitted — Bearer token auth does not
                // use cookies, and enabling it unnecessarily restricts allowed origins.
            });
        });

        services.Configure<AgentHubConfig>(
            configuration.GetSection("AppConfig:AgentHub"));

        services.Configure<PrometheusConfig>(
            configuration.GetSection("AppConfig:Prometheus"));

        var promConfig = configuration.GetSection("AppConfig:Prometheus").Get<PrometheusConfig>() ?? new PrometheusConfig();
        if (promConfig.EnableDemoData)
        {
            services.AddSingleton<IPrometheusQueryService, DemoMetricsService>();
        }
        else
        {
            services.AddHttpClient<IPrometheusQueryService, PrometheusQueryService>((sp, client) =>
            {
                var config = sp.GetRequiredService<IOptions<PrometheusConfig>>().Value;
                client.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + '/');
                client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
            });
        }

        // SLO evaluator — evaluates configured SLO targets against Prometheus
        services.AddSingleton<ISloEvaluator, SloEvaluationService>();

        // NullMcpPromptProvider is the default when no real implementation is registered.
        // Real implementations (e.g. from Infrastructure) override this via AddSingleton<IMcpPromptProvider, T>
        // registered after this call, since TryAdd only sets if not already present.
        services.TryAddSingleton<IMcpPromptProvider, NullMcpPromptProvider>();

        // Singleton: FileSystemConversationStore owns a SemaphoreSlim for thread-safety;
        // a scoped/transient registration would create multiple semaphore instances.
        services.AddSingleton<IConversationStore, FileSystemConversationStore>();

        // Singleton: ConversationLockRegistry must outlive hub instances (hubs are transient).
        services.AddSingleton<ConversationLockRegistry>();

        // Singleton: ConnectionTracker replaces the static ConcurrentDictionary on the hub.
        services.AddSingleton<IConnectionTracker, ConnectionTracker>();

        // Scoped: ConversationOrchestrator owns conversation lifecycle, turn dispatch, and metrics.
        // The hub delegates all business logic here and handles only SignalR transport.
        services.AddScoped<IConversationOrchestrator, ConversationOrchestrator>();

        services.AddSingleton<IAgUiEventWriterAccessor, AgUiEventWriterAccessor>();
        services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
        services.AddSingleton<IDriftNotificationChannel, AgUiDriftNotifier>();
        services.AddSingleton<ILearningNotificationChannel, AgUiLearningNotifier>();
        services.AddSingleton<IPlanProgressNotifier, AgUiPlanProgressNotifier>();

        // Override the no-op IEvalRunNotifier wired by GetServices() with the SignalR-backed
        // implementation. Last-registration-wins: this AddSingleton replaces the prior
        // NullEvalRunNotifier registration when the host wires both extension methods.
        services.AddSingleton<
            Application.AI.Common.Evaluation.Interfaces.IEvalRunNotifier,
            Notifications.SignalREvalRunNotifier>();

        // Override the no-op IContextSnapshotNotifier wired by GetServices() with the
        // SignalR + observability-store-backed implementation. Singleton matches
        // SignalREvalRunNotifier and IObservabilityStore lifetimes.
        services.AddSingleton<
            Application.AI.Common.Interfaces.Context.IContextSnapshotNotifier,
            Notifications.SignalRContextSnapshotNotifier>();

        // Scoped: AgUiRunHandler takes per-request dependencies (ClaimsPrincipal, CancellationToken).
        services.AddScoped<AgUi.AgUiRunHandler>();

        services.AddHostedService<SessionIdleCleanupService>();

        // SignalRSpanExporter bridges OTel Activity pipeline → SignalR.
        // Registered as singleton so the same instance is both the IHostedService (drain loop)
        // and the BaseExporter<Activity> added to the OTel tracing pipeline below.
        services.AddSingleton<SignalRSpanExporter>();
        services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());

        // Append SignalRSpanExporter to the OTel tracing pipeline AFTER GetServices() has run
        // and Infrastructure.Observability's ITelemetryConfigurator (order 300) has already
        // registered Jaeger / Azure Monitor exporters. Using AddOpenTelemetry().WithTracing()
        // here appends without touching Infrastructure.Observability's DI code.
        // AgentHubSpanExportProcessor is a file-private concrete subclass of
        // SimpleExportProcessor<Activity> (which is abstract to prevent direct instantiation).
        services.AddOpenTelemetry()
            .WithTracing(b => b.AddProcessor(
                sp => new AgentHubSpanExportProcessor(
                    sp.GetRequiredService<SignalRSpanExporter>())));

        return services;
    }
}

/// <summary>
/// Concrete <see cref="SimpleExportProcessor{T}"/> wrapping <see cref="SignalRSpanExporter"/> for
/// registration in the OTel tracing pipeline. File-scoped to keep it an implementation detail of
/// <see cref="DependencyInjection"/>.
/// </summary>
file sealed class AgentHubSpanExportProcessor(SignalRSpanExporter exporter)
    : SimpleExportProcessor<Activity>(exporter);
