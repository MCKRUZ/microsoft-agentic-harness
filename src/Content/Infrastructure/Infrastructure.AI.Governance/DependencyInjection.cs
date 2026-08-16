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
/// Registers Agent Governance Toolkit services and harness adapter implementations.
/// Call from the composition root when <c>GovernanceConfig.Enabled</c> is true.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds AGT-backed governance services to the service collection.
    /// Registers the <see cref="GovernanceKernel"/> as a singleton and wires
    /// adapter implementations for all governance interfaces.
    /// </summary>
    public static IServiceCollection AddGovernanceDependencies(
        this IServiceCollection services,
        GovernanceConfig config)
    {
        // Policy files are only ever relevant to the declarative YAML layer, which Enabled alone
        // governs (#386) — EnablePromptInjectionDetection and EnableMcpSecurity can bring up this
        // whole method without wanting the policy layer at all, and PolicyPaths should not even be
        // touched in that case.
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

        services.AddSingleton(kernel);
        services.AddSingleton<AuditLogger>();

        // Deliberately not registering the raw PolicyEngine as its own resolvable service (unlike
        // AuditLogger above): PolicyEngine.LoadYamlFile/LoadYaml/LoadPolicy all bypass PolicyYamlGuard,
        // so a consumer that could resolve PolicyEngine directly and load its own YAML through it would
        // reintroduce #384. Only the adapter needs it, so it's captured in this factory instead.
        //
        // Conditional on Enabled (#386): a consumer that armed this method solely for
        // EnablePromptInjectionDetection or EnableMcpSecurity did not opt into the declarative policy
        // layer, so IGovernancePolicyEngine resolves the same NoOpPolicyEngine it would have gotten
        // from AddGovernanceNoOpDependencies — the policy layer's own switch, not a side effect of
        // some other feature area being on.
        services.AddSingleton<IGovernancePolicyEngine>(_ => config.Enabled
            ? new AgtPolicyEngineAdapter(kernel.PolicyEngine)
            : new NoOpPolicyEngine());

        // Prompt-injection detection is optional. The AGT kernel only builds an InjectionDetector when
        // GovernanceConfig.EnablePromptInjectionDetection is true, so registering kernel.InjectionDetector
        // (or the AgtPromptInjectionAdapter that requires it) unconditionally crashes composition for the
        // valid Enabled=true, EnablePromptInjectionDetection=false configuration — AddSingleton throws on a
        // null instance. When detection is off, satisfy IPromptInjectionScanner with the no-op scanner so
        // every consumer resolves and the PromptInjectionBehavior simply passes through.
        if (config.EnablePromptInjectionDetection)
        {
            // Fail closed: if detection is configured on, the kernel must have produced a detector.
            // Silently falling back to the no-op scanner here would leave a *configured* security
            // control inert — the exact silent-degradation failure this hardening pass targets.
            if (kernel.InjectionDetector is null)
            {
                throw new InvalidOperationException(
                    "GovernanceConfig.EnablePromptInjectionDetection is true but the governance kernel " +
                    "produced no InjectionDetector; refusing to start with the configured control disabled.");
            }

            services.AddSingleton(kernel.InjectionDetector);
            services.AddSingleton<IPromptInjectionScanner, AgtPromptInjectionAdapter>();
        }
        else
        {
            services.AddSingleton<IPromptInjectionScanner, NoOpInjectionScanner>();
        }

        services.AddSingleton<IGovernanceAuditService, AgtAuditAdapter>();
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
    /// Used when <c>GovernanceConfig.Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddGovernanceNoOpDependencies(
        this IServiceCollection services)
    {
        services.AddSingleton<IGovernancePolicyEngine, NoOpPolicyEngine>();
        services.AddSingleton<IPromptInjectionScanner, NoOpInjectionScanner>();
        services.AddSingleton<IGovernanceAuditService, NoOpAuditService>();
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
