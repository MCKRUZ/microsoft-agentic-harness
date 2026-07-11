using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Domain.Common.Config.AI.BundleExecution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Presentation.BundleApi.Services;
using Presentation.BundleApi.Streaming;

namespace Presentation.BundleApi.Extensions;

/// <summary>
/// Host-specific service registration for <c>Presentation.BundleApi</c>: controllers, the fail-closed
/// authentication scheme (its own audience), authorization, and per-path rate limiting. The cross-layer
/// wiring (MediatR handlers, bundle stores, background services) is composed separately by
/// <c>Presentation.Common</c>'s <c>GetServices</c>, exactly as the other hosts do.
/// </summary>
public static class BundleApiServiceCollectionExtensions
{
    /// <summary>Rate-limit policy applied to the whole controller (run, poll, delete).</summary>
    public const string DefaultRateLimitPolicy = "bundles";

    /// <summary>Stricter rate-limit policy for registration — staging an archive is comparatively expensive.</summary>
    public const string RegisterRateLimitPolicy = "bundles-register";

    /// <summary>
    /// Per-caller <em>concurrency</em> policy for the live-stream endpoint. A streamed run executes inline on
    /// its connection for the whole conversation and bypasses the single-threaded background dispatcher, so the
    /// fixed-window request-rate limiter (which counts starts, not simultaneous connections) cannot bound it.
    /// A concurrency limiter holds each permit for the connection's lifetime, capping how many agent
    /// conversations one caller can drive at once.
    /// </summary>
    public const string StreamRateLimitPolicy = "bundles-stream";

    /// <summary>
    /// Registers the bundle API's controllers, authentication, authorization, and rate limiters.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration, read for <c>AppConfig:AI:BundleExecution:Auth</c>.</param>
    public static IServiceCollection AddBundleApiServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddControllers()
            .AddJsonOptions(options =>
                // Serialize BundleRunStatus and friends as their names, not ordinals, so the poll contract
                // is human-readable and stable against enum renumbering.
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var bundleConfig = configuration
            .GetSection("AppConfig:AI:BundleExecution")
            .Get<BundleExecutionConfig>() ?? new BundleExecutionConfig();

        // Drives the opt-in live SSE feed: arms the assistant-text sink and calls the shared run executor.
        // Stateless — per-request state is local to each call.
        services.AddTransient<BundleRunStreamer>();

        services.AddBundleApiAuthentication(bundleConfig.Auth);

        // Bound the multipart upload at the transport boundary to the same limit the staging service enforces,
        // so an oversized archive is rejected before MVC buffers the whole body to a temp file — the app's
        // declared MaxArchiveBytes is the first line of defence, not a post-buffering afterthought.
        services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = bundleConfig.MaxArchiveBytes);

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(DefaultRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(ResolvePartitionKey(httpContext), _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));

            options.AddPolicy(RegisterRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(ResolvePartitionKey(httpContext), _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

            // Concurrency (not rate): each open stream holds a permit for its whole lifetime, so a caller can
            // drive at most MaxConcurrentStreamsPerCaller agent conversations at once. QueueLimit 0 rejects a
            // caller's excess connections outright rather than parking them.
            var maxStreams = Math.Max(1, bundleConfig.MaxConcurrentStreamsPerCaller);
            options.AddPolicy(StreamRateLimitPolicy, httpContext =>
                RateLimitPartition.GetConcurrencyLimiter(ResolvePartitionKey(httpContext), _ =>
                    new ConcurrencyLimiterOptions { PermitLimit = maxStreams, QueueLimit = 0 }));
        });

        return services;
    }

    /// <summary>
    /// Partitions the rate limiter per caller so one client cannot exhaust a shared global window and starve
    /// every other caller. Keys on the caller's stable, per-principal-unique id (never the non-unique display
    /// name), falling back to the remote IP — the only distinguishing signal in the anonymous dev mode, where
    /// every request shares one synthetic principal — then a constant last resort.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext httpContext)
    {
        var stableId = BundleCallerIdentity.StableId(httpContext.User);
        if (stableId is not null)
            return $"user:{stableId}";

        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(ip) ? "unknown" : $"ip:{ip}";
    }

    /// <summary>
    /// Installs the bundle API's authentication scheme, fail-closed. The host refuses to start unless a valid
    /// Entra scheme is configured or a developer has consciously opted into anonymous serving.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="auth">The bundle API auth configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// Authentication is unconfigured without the anonymous opt-in, or the anonymous opt-in is contradictorily
    /// combined with a configured scheme.
    /// </exception>
    public static IServiceCollection AddBundleApiAuthentication(
        this IServiceCollection services, BundleApiAuthConfig auth)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(auth);

        if (auth.AllowAnonymous && auth.IsConfigured)
            throw new InvalidOperationException(
                "AppConfig:AI:BundleExecution:Auth is contradictory: AllowAnonymous=true cannot be combined " +
                "with a configured scheme (TenantId + ClientId). Remove AllowAnonymous to enforce the scheme, " +
                "or clear TenantId/ClientId to run anonymously.");

        // A half-configured scheme (exactly one of TenantId/ClientId) is a misconfiguration, not an implicit
        // request to serve anonymously — fail closed so a forgotten identifier never silently opens the door.
        if (auth.IsPartiallyConfigured)
            throw new InvalidOperationException(
                "AppConfig:AI:BundleExecution:Auth is half-configured — exactly one of TenantId/ClientId is set. " +
                "Supply BOTH to enforce Entra validation, or clear both (and set AllowAnonymous=true for local " +
                "development) to serve anonymously. Refusing to start (fail-closed).");

        if (!auth.IsConfigured)
        {
            if (!auth.AllowAnonymous)
                throw new InvalidOperationException(
                    "Bundle API authentication is not configured — refusing to start (fail-closed). Set " +
                    "AppConfig:AI:BundleExecution:Auth:TenantId and :ClientId to this API's own Entra audience. " +
                    "For local development only, authentication can be consciously disabled with " +
                    "AppConfig:AI:BundleExecution:Auth:AllowAnonymous=true; running under Environment=Development " +
                    "alone does not disable it.");

            // Explicit anonymous opt-in — boot open, loudly. A permissive handler authenticates every
            // request as a synthetic principal so the controller's [Authorize] is satisfied; the capability
            // envelope still resolves to the fail-closed default (no subject), so an anonymous run is confined.
            services
                .AddAuthentication(AnonymousAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AnonymousAuthenticationHandler>(
                    AnonymousAuthenticationHandler.SchemeName, _ => { });
            services.AddAuthorization();
            services.AddHostedService<BundleApiAnonymousModeStartupWarning>();
            return services;
        }

        var authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";
        var audience = $"api://{auth.ClientId}";

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers =
                    [
                        $"https://sts.windows.net/{auth.TenantId}/",
                        $"https://login.microsoftonline.com/{auth.TenantId}/v2.0"
                    ],
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero // repo security baseline: no grace window
                };
            });

        // Fallback policy closes the unmapped-endpoint gap: any endpoint added without explicit authorization
        // metadata still requires an authenticated caller.
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}
