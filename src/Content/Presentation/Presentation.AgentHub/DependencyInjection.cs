using System.Diagnostics;
using System.Threading.RateLimiting;
using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Web;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Presentation.AgentHub.Auth;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Services;
using Presentation.AgentHub.Telemetry;

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
        services.AddControllers();

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

        services.AddSignalR();

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

            // Token bucket for SignalR hub send messages — applied in section-04
            // via [EnableRateLimiting("HubSendMessage")] on the hub method.
            options.AddTokenBucketLimiter("HubSendMessage", o =>
            {
                o.TokenLimit = 10;
                o.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
                o.TokensPerPeriod = 10;
                o.AutoReplenishment = true;
            });

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

        // NullMcpPromptProvider is the default when no real implementation is registered.
        // Real implementations (e.g. from Infrastructure) override this via AddSingleton<IMcpPromptProvider, T>
        // registered after this call, since TryAdd only sets if not already present.
        services.TryAddSingleton<IMcpPromptProvider, NullMcpPromptProvider>();

        // Singleton: FileSystemConversationStore owns a SemaphoreSlim for thread-safety;
        // a scoped/transient registration would create multiple semaphore instances.
        services.AddSingleton<IConversationStore, FileSystemConversationStore>();

        // Singleton: ConversationLockRegistry must outlive hub instances (hubs are transient).
        services.AddSingleton<ConversationLockRegistry>();

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
