using Application.AI.Common;
using Application.Common;
using Application.Common.Interfaces.Security;
using Application.Core;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Resilience;
using Domain.Common.Config.Azure;
using Domain.Common.Config.Cache;
using Domain.Common.Config.Connectors;
using Domain.Common.Config.Http;
using Domain.Common.Config.Infrastructure;
using Domain.Common.Config.Observability;
using Infrastructure.AI;
using Infrastructure.AI.Connectors;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Governance;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.Prompts;
using Infrastructure.AI.RAG;
using Infrastructure.AI.MCP;
using Infrastructure.APIAccess;
using Infrastructure.Common;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Presentation.Common.Helpers;
using Presentation.Common.Hosting;
using Presentation.Common.Security;

namespace Presentation.Common.Extensions;

/// <summary>
/// Master orchestrator for service registration. Composes configuration binding,
/// caching, health checks, project dependencies (Application + Infrastructure),
/// OpenTelemetry, and authentication into a single <c>GetServices()</c> call.
/// </summary>
/// <remarks>
/// <para>
/// This is the primary entry point for the Presentation composition root.
/// A minimal <c>Program.cs</c> only needs:
/// <code>
/// builder.Services.GetServices();
/// </code>
/// All layer-specific DI registrations are wired internally.
/// </para>
/// <para>
/// <strong>Registration order matters:</strong> project dependencies are registered
/// before OpenTelemetry so that all <c>ITelemetryConfigurator</c> implementations
/// are available when the OTel pipeline is built.
/// </para>
/// </remarks>
public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Binds all <c>AppConfig</c> subsections to their strongly-typed configuration classes
    /// using the Options pattern (<c>IOptionsMonitor&lt;T&gt;</c>).
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">
    /// The root <see cref="IConfiguration"/> containing the <c>AppConfig</c> section.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Each subsection path maps to <c>AppConfig:{SectionName}</c> in appsettings.json.
    /// All config classes support runtime reload via <c>IOptionsMonitor&lt;T&gt;</c>.
    /// </remarks>
    public static IServiceCollection RegisterConfigSections(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AppConfig>(configuration.GetSection("AppConfig"));
        services.Configure<CommonConfig>(configuration.GetSection("AppConfig:Common"));
        services.Configure<LoggingConfig>(configuration.GetSection("AppConfig:Logging"));
        services.Configure<AgentConfig>(configuration.GetSection("AppConfig:Agent"));
        services.Configure<HttpConfig>(configuration.GetSection("AppConfig:Http"));
        services.Configure<InfrastructureConfig>(configuration.GetSection("AppConfig:Infrastructure"));
        services.Configure<ConnectorsConfig>(configuration.GetSection("AppConfig:Connectors"));
        services.Configure<ObservabilityConfig>(configuration.GetSection("AppConfig:Observability"));
        services.Configure<AIConfig>(configuration.GetSection("AppConfig:AI"));
        services.Configure<EmbeddingConfig>(configuration.GetSection("AppConfig:AI:Embedding"));
        services.Configure<AzureConfig>(configuration.GetSection("AppConfig:Azure"));
        services.Configure<CacheConfig>(configuration.GetSection("AppConfig:Cache"));
        services.Configure<EscalationConfig>(configuration.GetSection("AppConfig:AI:Governance:Escalation"));
        services.Configure<ResilienceConfig>(configuration.GetSection("AppConfig:AI:Resilience"));
        services.Configure<Domain.Common.Config.AI.DriftDetection.DriftDetectionConfig>(
            configuration.GetSection("AppConfig:AI:DriftDetection"));
        services.Configure<Domain.Common.Config.AI.Learnings.LearningsConfig>(
            configuration.GetSection("AppConfig:AI:Learnings"));
        // Sandbox capability-enforcement knobs (SandboxConfig). Bound under a distinct
        // path from AppConfig:AI:Sandbox (which binds the unrelated SandboxOptions class).
        // Composes over the AddOptions<SandboxConfig>() defaults registered in
        // Application.AI.Common so operator-set DefaultGrantedCapabilities / ToolOverrides /
        // WorkspaceRoot / Enabled actually reach IOptionsMonitor<SandboxConfig> consumers.
        services.Configure<Domain.Common.Config.AI.Sandbox.SandboxConfig>(
            configuration.GetSection("AppConfig:AI:SandboxCapabilities"));

        return services;
    }

    /// <summary>
    /// Top-level entry point that loads configuration, binds all sections, and
    /// registers every service the application needs.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="includeHealthChecksUI">
    /// When <c>true</c> (default), registers the HealthChecks UI with in-memory storage.
    /// Set to <c>false</c> for console/worker applications that have no HTTP pipeline.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// This method:
    /// <list type="number">
    ///   <item>Loads configuration via <see cref="AppConfigHelper.LoadAppConfig"/></item>
    ///   <item>Binds all config sections via <see cref="RegisterConfigSections"/></item>
    ///   <item>Delegates to <see cref="BuildGlobalSolutionServices"/> for all service registrations</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection GetServices(
        this IServiceCollection services,
        bool includeHealthChecksUI = true)
    {
        var config = AppConfigHelper.LoadAppConfig();

        services.RegisterConfigSections(config);

        var appConfig = config.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();

        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI);

        return services;
    }

    /// <summary>
    /// Registers all cross-cutting services: options, caching, health checks,
    /// project dependencies, and OpenTelemetry.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">The fully bound application configuration.</param>
    /// <param name="includeHealthChecksUI">
    /// When <c>true</c>, registers the HealthChecks UI with in-memory storage.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Order is intentional:</strong> project dependencies register
    /// <c>ITelemetryConfigurator</c> implementations that the OTel pipeline
    /// discovers and applies. OTel must be registered last.
    /// </para>
    /// </remarks>
    public static IServiceCollection BuildGlobalSolutionServices(
        this IServiceCollection services,
        AppConfig appConfig,
        bool includeHealthChecksUI = true)
    {
        services.AddOptions();
        services.AddProblemDetails();
        services.AddHttpContextAccessor();

        // Console-style hosts (ConsoleUI, EvalRunner, FoundryHost) compose a bare ServiceCollection
        // with no IHost, so IHostEnvironment is never registered — yet services like
        // AutonomyDecisionEvaluator hard-inject it and fail at first resolution. TryAdd fills the gap
        // for those hosts while leaving a web host's real IHostEnvironment untouched.
        services.TryAddSingleton<IHostEnvironment>(new HarnessHostEnvironment());

        services.AddCacheConfiguration(appConfig.Cache);
        services.AddCustomHealthChecks(appConfig, includeHealthChecksUI);

        // Project dependencies BEFORE telemetry so ITelemetryConfigurator implementations are registered
        services.AddGlobalProjectDependencies(appConfig);

        // OTel pipeline (must be after project deps to pick up configurators)
        services.AddOpenTelemetry(appConfig);

        // Fail-fast guard registered last: resolves critical options bindings and services
        // at host start so a missing DI registration or config binding fails loudly at boot
        // instead of silently doing nothing at runtime ("built-but-never-wired" defense).
        services.AddHostedService<Startup.StartupRegistrationSmokeCheck>();

        return services;
    }

    /// <summary>
    /// Configures the caching strategy based on <see cref="CacheConfig.CacheType"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="cacheConfig">The cache configuration section.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Supported strategies:
    /// <list type="bullet">
    ///   <item><see cref="CacheType.None"/> / <see cref="CacheType.Memory"/> —
    ///     <c>AddMemoryCache</c> + <c>AddDistributedMemoryCache</c></item>
    ///   <item><see cref="CacheType.DistributedMemory"/> — <c>AddDistributedMemoryCache</c> only</item>
    ///   <item><see cref="CacheType.RedisCache"/> — <c>AddStackExchangeRedisCache</c> with
    ///     endpoint, password, service name, and client name from <see cref="RedisClientConfig"/></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddCacheConfiguration(
        this IServiceCollection services,
        CacheConfig cacheConfig)
    {
        switch (cacheConfig.CacheType)
        {
            case CacheType.DistributedMemory:
                services.AddDistributedMemoryCache();
                break;

            case CacheType.RedisCache:
                services.AddStackExchangeRedisCache(options =>
                {
                    var redis = cacheConfig.RedisClient;
                    options.Configuration = redis.Endpoint;
                    options.ConfigurationOptions = new StackExchange.Redis.ConfigurationOptions
                    {
                        EndPoints = { redis.Endpoint },
                        Password = redis.Secret,
                        ServiceName = redis.ServiceName,
                        ClientName = redis.ClientId
                    };
                });
                break;

            case CacheType.None:
            case CacheType.Memory:
            default:
                services.AddMemoryCache();
                services.AddDistributedMemoryCache();
                break;
        }

        return services;
    }

    /// <summary>
    /// Registers all Application and Infrastructure layer dependencies in the
    /// correct order. Application layers first, then Infrastructure implementations.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">The full application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registration order:
    /// <list type="number">
    ///   <item>Application.Common — MediatR, FluentValidation, pipeline behaviors</item>
    ///   <item>Application.AI.Common — Agent pipeline behaviors, AI telemetry configurator</item>
    ///   <item>Infrastructure.Common — Identity, HTTP authorization config</item>
    ///   <item>Infrastructure.AI — Tools, state management, file system, persistent agents</item>
    ///   <item>Infrastructure.AI.Connectors — External API connector clients</item>
    ///   <item>Infrastructure.AI.MCP — MCP client connection manager</item>
    ///   <item>Infrastructure.APIAccess — HTTP client factory, resilience, auth handlers</item>
    ///   <item>Infrastructure.Observability — OTel pipeline configurator (Order 300)</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddGlobalProjectDependencies(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        // Application layer
        services.AddApplicationCommonDependencies(appConfig.Logging);
        services.AddApplicationAIDependencies();
        services.AddApplicationCoreDependencies();

        // User identity — system user for console/worker, replace with HttpContextUser for web
        services.AddScoped<IUser, SystemUser>();

        // Infrastructure layer
        services.AddInfrastructureCommonDependencies();
        services.AddKnowledgeGraphDependencies(appConfig);
        // RAG must register before Infrastructure.AI — tool registrations depend on IRagOrchestrator
        services.AddRagDependencies(appConfig);
        services.AddInfrastructureAIDependencies(appConfig);
        if (appConfig.AI?.Governance is { Enabled: true } govConfig)
            services.AddGovernanceDependencies(govConfig);
        else
            services.AddGovernanceNoOpDependencies();
        services.AddAIConnectors();
        services.AddMcpClientDependencies();
        services.AddInfrastructureApiAccessDependencies();
        services.AddInfrastructureObservabilityDependencies();

        // Prompt registry (Sub-phase 5.3) — locates the repo's top-level `prompts/`
        // folder by walking up from the process base directory. When not found, the
        // registry returns empty lookups (no exception) so hosts with no prompts boot
        // cleanly. PromptUsageOptions flows from the AppConfig tree
        // (AppConfig:AI:PromptUsage) into the registry directly — the registry takes the
        // options instance rather than resolving IOptions<PromptUsageOptions>, so this
        // is the only path by which PersistenceEnabled can be turned on from config.
        services.AddPromptRegistry(
            promptsRootPath: LocatePromptsRoot(),
            usageOptions: appConfig.AI?.PromptUsage ?? new PromptUsageOptions());

        // Eval framework (Infrastructure.AI.Evaluation runners + metrics + reporters)
        // is NOT registered here — it is opt-in for the EvalRunner CLI only. Web/console
        // hosts that don't run evaluations should not carry HarnessAgentInvoker, the six
        // metric singletons, three reporters, and the YAML loader on every cold start.
        // Call services.AddEvaluationDependencies() explicitly from the eval host.

        // Eval dashboard persistence (Sub-phase 5.4) IS registered here so the dashboard
        // host can resolve IEvalRunStore for ingest + history queries. When
        // PersistenceEnabled is false (default), AddEvalDashboardPersistence wires the
        // NullEvalRunStore so handlers resolve cleanly without an opt-in flag elsewhere.
        services.AddEvalDashboardPersistence(
            appConfig.AI?.EvalDashboard ?? new EvalDashboardOptions());

        // Default IEvalRunNotifier is the no-op for hosts without a real-time transport
        // (CLI, worker). The dashboard host overrides via AddSingleton<IEvalRunNotifier,
        // SignalREvalRunNotifier>() after this call — TryAddSingleton would prevent the
        // override, AddSingleton-last-wins is the intended semantic here.
        services.AddSingleton<
            Application.AI.Common.Evaluation.Interfaces.IEvalRunNotifier,
            Application.AI.Common.Evaluation.Notifications.NullEvalRunNotifier>();

        // Foresight context-snapshot pipeline (PR 3). DefaultContextSnapshotComputer
        // is a pure function — singleton fine. Null notifier same last-write-wins
        // pattern as IEvalRunNotifier above; AgentHub host overrides with the
        // SignalR + observability-store-backed implementation.
        services.AddSingleton<
            Application.AI.Common.Interfaces.Context.IContextSnapshotComputer,
            Application.AI.Common.Categorization.DefaultContextSnapshotComputer>();
        services.AddSingleton<
            Application.AI.Common.Interfaces.Context.IContextSnapshotNotifier,
            Application.AI.Common.Notifications.NullContextSnapshotNotifier>();

        return services;
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for a top-level
    /// <c>prompts/</c> folder (the repo-root convention). Returns the discovered path
    /// when found, or a sentinel path that the registry treats as "no prompts" when
    /// not — supports both repo-checkout layouts and trimmed published binaries.
    /// </summary>
    private static string LocatePromptsRoot()
    {
        // Anchor on a `.prompts-root` marker file so the walk-up doesn't match unrelated
        // `Prompts/` source directories on case-insensitive filesystems (Windows/macOS).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, ".prompts-root")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        // Fallback: deterministic path that doesn't exist; FilePromptRegistry handles
        // missing roots by returning empty lookups.
        return Path.Combine(AppContext.BaseDirectory, "prompts");
    }

    /// <summary>
    /// Configures authentication and authorization. When Azure AD B2C is configured,
    /// sets up JWT Bearer auth with Microsoft Identity Web. Otherwise, registers
    /// basic authentication and authorization services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="azureConfig">
    /// Azure configuration containing AD B2C instance, domain, and policy settings.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When <see cref="AzureADB2CConfig.Instance"/> is null or empty, only
    /// <c>AddAuthentication()</c> and <c>AddAuthorization()</c> are registered.
    /// This supports local development without Azure AD.
    /// </para>
    /// <para>
    /// When B2C is configured, JWT validation enforces:
    /// <list type="bullet">
    ///   <item>Lifetime validation with zero clock skew</item>
    ///   <item>Issuer and audience validation</item>
    ///   <item>Signing key validation</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAuthDependencies(
        this IServiceCollection services,
        AzureConfig azureConfig)
    {
        services.AddAuthorization();

        if (string.IsNullOrEmpty(azureConfig.ADB2C.Instance))
        {
            services.AddAuthentication();
            return services;
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(
                jwtOptions =>
                {
                    jwtOptions.TokenValidationParameters.ValidateLifetime = true;
                    jwtOptions.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                    jwtOptions.TokenValidationParameters.ValidateIssuer = true;
                    jwtOptions.TokenValidationParameters.ValidateAudience = true;
                    jwtOptions.TokenValidationParameters.ValidateIssuerSigningKey = true;
                },
                identityOptions =>
                {
                    var b2c = azureConfig.ADB2C;
                    identityOptions.Instance = b2c.Instance;
                    identityOptions.Domain = b2c.Domain;
                    identityOptions.SignUpSignInPolicyId = b2c.SignUpSignInPolicyId;
                    identityOptions.SignedOutCallbackPath = b2c.SignedOutCallbackPath;
                });

        return services;
    }

}
