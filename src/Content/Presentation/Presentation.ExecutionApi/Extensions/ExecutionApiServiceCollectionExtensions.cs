using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Planner;
using System.Threading.RateLimiting;
using Domain.Common.Config.AI.BundleExecution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Infrastructure.AI.Runs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Presentation.ExecutionApi.Services;
using Presentation.ExecutionApi.Streaming;

namespace Presentation.ExecutionApi.Extensions;

/// <summary>
/// Host-specific service registration for <c>Presentation.ExecutionApi</c>: controllers, the fail-closed
/// authentication scheme (its own audience), authorization, and per-path rate limiting. The cross-layer
/// wiring (MediatR handlers, bundle stores, background services) is composed separately by
/// <c>Presentation.Common</c>'s <c>GetServices</c>, exactly as the other hosts do.
/// </summary>
public static class ExecutionApiServiceCollectionExtensions
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
    /// Per-caller <em>concurrency</em> policy for direct tool invocation, for the same reason the stream
    /// endpoint above has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An invocation runs synchronously on its request thread for up to <c>InvocationTimeout</c>, holding
    /// a thread, a DI scope, and whatever the tool itself acquired — it does not go through the
    /// background dispatcher the way a bundle or workflow run does. The controller's fixed-window
    /// limiter counts <em>starts</em>, so it cannot bound that: a caller can open its whole per-minute
    /// allowance against a slow tool, still have most of them executing when the window rolls, and be
    /// admitted for another allowance on top.
    /// </para>
    /// <para>
    /// A concurrency limiter holds each permit for the invocation's lifetime instead, so what is capped
    /// is work in flight rather than requests begun. <c>QueueLimit = 0</c> refuses a caller's excess
    /// outright rather than parking it, because a parked request holds a connection to no purpose.
    /// </para>
    /// <para>
    /// <strong>This replaces the controller's fixed window for this action rather than adding to it</strong>
    /// — only the attribute closest to the endpoint applies — which is the same trade the stream
    /// endpoint makes above. It is the right way round: a caller capped at
    /// <see cref="MaxConcurrentInvocationsPerCaller"/> simultaneous invocations cannot exhaust the
    /// thread pool however fast it calls, whereas a request-rate cap cannot bound work that is still
    /// running when the window rolls. What the swap gives up is real and worth stating plainly: this
    /// action has no request-<em>rate</em> bound at all, and this host configures no
    /// <c>GlobalLimiter</c> to fall back on. An authorized caller may issue unlimited invocations of a
    /// fast tool, four at a time. That is the lesser exposure — a fast tool returns its thread
    /// immediately, so the cost is throughput rather than capacity — but it is not nothing, and
    /// chaining a fixed window with this concurrency limiter is the obvious follow-up.
    /// </para>
    /// </remarks>
    public const string InvokeRateLimitPolicy = "tools-invoke";

    /// <summary>
    /// How many direct tool invocations one caller may have executing at once.
    /// </summary>
    /// <remarks>
    /// Deliberately low. Direct invocation is off by default and expected to serve automation making a
    /// few targeted calls, not bulk traffic; a caller that genuinely needs breadth should be running a
    /// workflow, which the dispatcher schedules and bounds host-wide.
    /// </remarks>
    private const int MaxConcurrentInvocationsPerCaller = 4;

    /// <summary>
    /// Registers the bundle API's controllers, authentication, authorization, and rate limiters.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration, read for <c>AppConfig:AI:BundleExecution:Auth</c>.</param>
    public static IServiceCollection AddExecutionApiServices(
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

        // Apply the configured body caps before the body is read. Registered as services (rather than
        // plain attributes) because each limit is an operator setting read live, not a compile-time
        // constant. One subclass per surface: the two caps bound different things — a whole workflow
        // graph versus one operation's arguments — and share only the mechanism.
        // Singleton, not scoped: each holds only IOptionsMonitor (itself a singleton) and reads
        // CurrentValue inside the filter callback, so there is no per-request state to keep.
        services.AddSingleton<WorkflowRequestSizeLimitFilter>();
        services.AddSingleton<ToolInvocationRequestSizeLimitFilter>();

        // Replaces the NullPlanProgressNotifier that Presentation.Common registers as the host-
        // overridable default, so this host's plan notifications reach the run progress broker instead
        // of being discarded. Last-write-wins, and this runs after the shared defaults.
        //
        // Singleton because it holds no per-request state and its two dependencies are singletons; a
        // scoped registration would fail to resolve on the dispatcher thread, which has no request.
        services.AddSingleton<IPlanProgressNotifier, PlanProgressRunBridge>();

        services.AddExecutionApiAuthentication(bundleConfig.Auth);

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

            // Concurrency, for the same reason as streams: a direct invocation occupies its request
            // thread for the whole call, so counting starts does not bound it.
            options.AddPolicy(InvokeRateLimitPolicy, httpContext =>
                RateLimitPartition.GetConcurrencyLimiter(ResolvePartitionKey(httpContext), _ =>
                    new ConcurrencyLimiterOptions
                    {
                        PermitLimit = MaxConcurrentInvocationsPerCaller,
                        QueueLimit = 0
                    }));
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
    public static IServiceCollection AddExecutionApiAuthentication(
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
            services.AddHostedService<ExecutionApiAnonymousModeStartupWarning>();
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
