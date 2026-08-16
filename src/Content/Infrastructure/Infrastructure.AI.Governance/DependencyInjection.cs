using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Policy;
using AgentGovernance.Security;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.Common.Factories;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Infrastructure.AI.Governance.Adapters;
using Infrastructure.AI.Governance.Classification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Governance;

/// <summary>
/// Registers Agent Governance Toolkit services and harness adapter implementations. Composition
/// roots call <see cref="AddGovernance"/>, which chooses this wiring or the no-op set based on
/// <see cref="GovernanceConfig.ArmsAgtKernel"/>.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds governance services to the service collection, choosing the AGT-backed implementation or
    /// the no-op set based on <see cref="GovernanceConfig.ArmsAgtKernel"/> — the single unconditional
    /// entry point composition roots should call, so a caller never has to ask <c>ArmsAgtKernel</c>
    /// first and branch between <see cref="AddGovernanceDependencies"/>/
    /// <see cref="AddGovernanceNoOpDependencies"/> itself. Both underlying methods stay public: unit
    /// tests that want to force one wiring or the other (e.g. proving the AGT path resolves correctly
    /// regardless of <see cref="GovernanceConfig.Enabled"/>) call them directly.
    /// </summary>
    public static IServiceCollection AddGovernance(
        this IServiceCollection services,
        GovernanceConfig config) =>
        config.ArmsAgtKernel
            ? services.AddGovernanceDependencies(config)
            : services.AddGovernanceNoOpDependencies();

    /// <summary>
    /// Adds AGT-backed governance services to the service collection.
    /// Registers the <see cref="GovernanceKernel"/> as a singleton and wires
    /// adapter implementations for all governance interfaces.
    /// </summary>
    public static IServiceCollection AddGovernanceDependencies(
        this IServiceCollection services,
        GovernanceConfig config)
    {
        var kernel = BuildKernel(config);
        services.AddSingleton(kernel);

        RegisterPolicyEngine(services, config, kernel);
        RegisterPromptInjectionDetection(services, config, kernel);
        RegisterAudit(services);

        services.AddSingleton<IMcpSecurityScanner, McpSecurityScannerAdapter>();
        services.AddSingleton<IMcpDefinitionPinStore, InMemoryMcpDefinitionPinStore>();
        services.AddSingleton<IMcpToolSurfaceScanner, McpToolSurfaceScannerAdapter>();

        services.AddSingleton<IResponseSanitizer, CredentialRedactor>();
        services.AddSingleton<IResponseSanitizer, ResponseInjectionScrubber>();
        services.AddSingleton<IResponseSanitizer, ExfiltrationUrlDetector>();
        services.AddSingleton<ICompositeResponseSanitizer, CompositeResponseSanitizer>();

        // Data-classification seam. The policy evaluator is pure; the provider routes to the Graph-backed
        // Information Protection and Purview Data Map clients when wired, else the fail-fast default.
        AddDataClassificationProvider(services, config.DataClassification);

        return services;
    }

    /// <summary>
    /// Constructs the <see cref="GovernanceKernel"/>, loading the declarative policy layer's YAML
    /// only when <see cref="GovernanceConfig.Enabled"/> is true — <see cref="GovernanceConfig.EnablePromptInjectionDetection"/>
    /// and <see cref="GovernanceConfig.EnableMcpSecurity"/> can bring the kernel up on their own
    /// (#386), and <see cref="GovernanceConfig.PolicyPaths"/> should not even be touched in that case.
    /// </summary>
    private static GovernanceKernel BuildKernel(GovernanceConfig config)
    {
        var policyContents = config.Enabled
            ? ReadAndValidatePolicyFiles(config.PolicyPaths)
            : [];

        var options = new GovernanceOptions
        {
            EnableAudit = config.EnableAudit,
            EnableMetrics = config.EnableMetrics,
            EnablePromptInjectionDetection = config.EnablePromptInjectionDetection,
            ConflictStrategy = (AgentGovernance.Policy.ConflictResolutionStrategy)(int)config.ConflictStrategy
        };

        // PolicyPaths deliberately left empty: GovernanceKernel's constructor would otherwise re-read
        // and re-parse every path itself via PolicyEngine.LoadYamlFile, duplicating the read
        // ReadAndValidatePolicyFiles already did. LoadPolicyFromYaml below reuses that same content.
        var kernel = new GovernanceKernel(options);
        foreach (var content in policyContents)
            kernel.LoadPolicyFromYaml(content);

        return kernel;
    }

    /// <summary>
    /// Registers <see cref="IGovernancePolicyEngine"/> — the real AGT-backed adapter when
    /// <see cref="GovernanceConfig.Enabled"/> is true, otherwise the no-op engine, since a consumer
    /// that armed <see cref="AddGovernanceDependencies"/> solely for
    /// <see cref="GovernanceConfig.EnablePromptInjectionDetection"/>,
    /// <see cref="GovernanceConfig.EnableMcpSecurity"/>, or
    /// <see cref="GovernanceConfig.EnableResponseSanitization"/> did not opt into the declarative
    /// policy layer (#386).
    /// </summary>
    /// <remarks>
    /// Deliberately not registering the raw <c>PolicyEngine</c> as its own resolvable service:
    /// <c>PolicyEngine.LoadYamlFile</c>/<c>LoadYaml</c>/<c>LoadPolicy</c> all bypass
    /// <see cref="PolicyYamlGuard"/>, so a consumer that could resolve <c>PolicyEngine</c> directly
    /// and load its own YAML through it would reintroduce #384. Only the adapter needs it, so it's
    /// captured in this factory instead.
    /// </remarks>
    private static void RegisterPolicyEngine(IServiceCollection services, GovernanceConfig config, GovernanceKernel kernel) =>
        services.AddSingleton<IGovernancePolicyEngine>(_ => config.Enabled
            ? new AgtPolicyEngineAdapter(kernel.PolicyEngine)
            : new NoOpPolicyEngine());

    /// <summary>
    /// Registers <see cref="IPromptInjectionScanner"/>. Detection is optional — the AGT kernel only
    /// builds an <c>InjectionDetector</c> when <see cref="GovernanceConfig.EnablePromptInjectionDetection"/>
    /// is true, so registering <c>kernel.InjectionDetector</c> (or the adapter that requires it)
    /// unconditionally would crash composition for the valid <c>Enabled=true,
    /// EnablePromptInjectionDetection=false</c> configuration — <c>AddSingleton</c> throws on a null
    /// instance. When detection is off, the no-op scanner satisfies every consumer and
    /// <c>PromptInjectionBehavior</c> simply passes through.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Detection is configured on but the kernel produced no <c>InjectionDetector</c>. Fails closed:
    /// silently falling back to the no-op scanner here would leave a <em>configured</em> security
    /// control inert.
    /// </exception>
    private static void RegisterPromptInjectionDetection(IServiceCollection services, GovernanceConfig config, GovernanceKernel kernel)
    {
        if (!config.EnablePromptInjectionDetection)
        {
            services.AddSingleton<IPromptInjectionScanner, NoOpInjectionScanner>();
            return;
        }

        if (kernel.InjectionDetector is null)
        {
            throw new InvalidOperationException(
                "GovernanceConfig.EnablePromptInjectionDetection is true but the governance kernel " +
                "produced no InjectionDetector; refusing to start with the configured control disabled.");
        }

        services.AddSingleton(kernel.InjectionDetector);
        services.AddSingleton<IPromptInjectionScanner, AgtPromptInjectionAdapter>();
    }

    /// <summary>
    /// Registers the real <see cref="IGovernanceAuditService"/>. Shared by
    /// <see cref="AddGovernanceDependencies"/> and <see cref="AddGovernanceNoOpDependencies"/>
    /// because <c>AuditLogger</c> has no dependency on <see cref="GovernanceKernel"/> — auditing is
    /// not one of the features <see cref="GovernanceConfig.ArmsAgtKernel"/> decides between, so both
    /// composition paths wire it identically instead of each declaring their own copy.
    /// </summary>
    /// <remarks>
    /// <c>AuditLogger</c>'s hash-chained entries are in-memory only — no trim, no eviction, no
    /// persistence — so this trail does not survive a process restart and grows for the process
    /// lifetime. Registering it unconditionally (this fix) widens that growth to every host that
    /// previously took the no-op path, since <see cref="GovernanceConfig.EnableAudit"/> defaults
    /// true. Tracked separately: https://github.com/MCKRUZ/microsoft-agentic-harness/issues/407.
    /// </remarks>
    private static void RegisterAudit(IServiceCollection services)
    {
        services.AddSingleton<AuditLogger>();
        services.AddSingleton<IGovernanceAuditService, AgtAuditAdapter>();
    }

    /// <summary>
    /// Resolves every configured policy path, then reads and validates each one via
    /// <see cref="PolicyYamlGuard"/> — the guard-covered replacement for the read
    /// <see cref="GovernanceKernel"/>'s constructor would otherwise perform itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A configured path doesn't resolve to a real file. A missing entry used to be silently dropped
    /// (<c>.Where(File.Exists)</c>), which could leave the whole declarative policy layer un-loaded —
    /// <c>HasPolicies</c> false, every tool call skipping this layer entirely — with no signal beyond a
    /// quieter-than-expected policy set. Fail loudly instead: the same silent-degradation class this
    /// method's other guard (mis-cased <c>default_action</c>, #384) exists to kill.
    /// </exception>
    private static List<string> ReadAndValidatePolicyFiles(IReadOnlyList<string> configuredPaths)
    {
        var resolvedPaths = configuredPaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p))
            .ToList();

        // ReadAndValidate below re-checks File.Exists per path, so a fully-present config pays one
        // redundant stat call per file — accepted deliberately, since this check's value isn't the
        // stat itself, it's reporting every missing path in one message instead of failing on the
        // first one ReadAndValidate happens to hit.
        var missing = resolvedPaths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "GovernanceConfig.PolicyPaths references files that do not exist: " +
                $"{string.Join(", ", missing)}. Refusing to start with a configured policy layer that " +
                "would silently load nothing.");

        return resolvedPaths.Select(PolicyYamlGuard.ReadAndValidate).ToList();
    }

    /// <summary>
    /// Registers the data-classification policy evaluator and the appropriate
    /// <see cref="IDataClassificationProvider"/>. When classification is enabled and at least one Purview
    /// world is switched on, wires those worlds — the Graph-backed <see cref="GraphSensitivityLabelClient"/>
    /// for documents/files and the <see cref="PurviewDataMapClient"/> for scanned cloud assets — behind a
    /// <see cref="RoutingDataClassificationProvider"/> and a shared TTL cache. Otherwise keeps the
    /// fail-fast default so an enabled-but-unconfigured gate fails loudly rather than silently allowing
    /// everything.
    /// </summary>
    internal static void AddDataClassificationProvider(IServiceCollection services, DataClassificationConfig config)
    {
        services.AddSingleton<IClassificationPolicyEvaluator, DefaultClassificationPolicyEvaluator>();

        var ip = config.InformationProtection;
        var dataMap = config.DataMap;
        if (config.Mode == ClassificationEnforcementMode.Off || (!ip.Enabled && !dataMap.Enabled))
        {
            services.AddSingleton<IDataClassificationProvider, NotConfiguredDataClassificationProvider>();
            return;
        }

        if (ip.Enabled)
            services.AddHttpClient(GraphSensitivityLabelClient.HttpClientName);

        if (dataMap.Enabled)
            services.AddHttpClient(PurviewDataMapClient.HttpClientName);

        services.AddSingleton<IDataClassificationProvider>(sp =>
        {
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();

            var router = new RoutingDataClassificationProvider(
                BuildInformationProtectionProvider(sp, ip, timeProvider, httpClientFactory),
                BuildDataMapProvider(sp, dataMap, timeProvider, httpClientFactory),
                timeProvider,
                sp.GetRequiredService<ILogger<RoutingDataClassificationProvider>>());

            return new CachedDataClassificationProvider(
                router,
                timeProvider,
                config.ResultCacheTtl,
                sp.GetRequiredService<ILogger<CachedDataClassificationProvider>>());
        });
    }

    /// <summary>
    /// Builds the Graph-backed Information Protection provider when that world is enabled, else null. The
    /// Entra credential is created lazily here (not at DI graph build) so a credential-config error
    /// surfaces when the provider is first resolved; Azure.Identity then caches and refreshes the token
    /// in-process for the lifetime of the singleton.
    /// </summary>
    private static IDataClassificationProvider? BuildInformationProtectionProvider(
        IServiceProvider sp,
        InformationProtectionProviderConfig ip,
        TimeProvider timeProvider,
        IHttpClientFactory httpClientFactory) =>
        ip.Enabled
            ? new GraphSensitivityLabelClient(
                httpClientFactory,
                AzureCredentialFactory.CreateTokenCredential(ip.Auth),
                ip,
                timeProvider,
                sp.GetRequiredService<ILogger<GraphSensitivityLabelClient>>())
            : null;

    /// <summary>
    /// Builds the Purview Data Map provider when that world is enabled, else null. The Entra credential is
    /// created lazily here (not at DI graph build) so a credential-config error surfaces when the provider
    /// is first resolved; Azure.Identity then caches and refreshes the token in-process for the lifetime of
    /// the singleton.
    /// </summary>
    private static IDataClassificationProvider? BuildDataMapProvider(
        IServiceProvider sp,
        DataMapProviderConfig dataMap,
        TimeProvider timeProvider,
        IHttpClientFactory httpClientFactory) =>
        dataMap.Enabled
            ? new PurviewDataMapClient(
                httpClientFactory,
                AzureCredentialFactory.CreateTokenCredential(dataMap.Auth),
                dataMap,
                timeProvider,
                sp.GetRequiredService<ILogger<PurviewDataMapClient>>())
            : null;

    /// <summary>
    /// Adds no-op governance services that satisfy DI without AGT.
    /// Used when <see cref="GovernanceConfig.ArmsAgtKernel"/> is false.
    /// </summary>
    /// <remarks>
    /// <see cref="IGovernanceAuditService"/> is the one exception to the no-op set — see
    /// <see cref="RegisterAudit"/>. <see cref="GovernanceConfig.EnableAudit"/> gates whether call
    /// sites actually invoke <c>.Log()</c> (<c>ToolInvocationGovernor</c>,
    /// <c>PromptInjectionBehavior</c>, etc.) — a no-op audit service here whenever every kernel-arming
    /// flag happened to be off would silently drop audit records for a consumer who left every other
    /// governance feature disabled but still wants an audit trail, the same fail-open class #386/#387
    /// exist to close.
    /// </remarks>
    public static IServiceCollection AddGovernanceNoOpDependencies(
        this IServiceCollection services)
    {
        services.AddSingleton<IGovernancePolicyEngine, NoOpPolicyEngine>();
        services.AddSingleton<IPromptInjectionScanner, NoOpInjectionScanner>();
        RegisterAudit(services);
        services.AddSingleton<IMcpSecurityScanner, NoOpMcpScanner>();
        services.AddSingleton<IMcpToolSurfaceScanner, NoOpMcpToolSurfaceScanner>();
        services.AddSingleton<ICompositeResponseSanitizer, NoOpResponseSanitizer>();

        // Data-classification seam (governance disabled): the pure evaluator plus a benign no-op
        // provider that classifies every asset as Unknown, so the dependency resolves without
        // contacting Purview or throwing.
        services.AddSingleton<IClassificationPolicyEvaluator, DefaultClassificationPolicyEvaluator>();
        services.AddSingleton<IDataClassificationProvider, NoOpDataClassificationProvider>();

        return services;
    }
}
