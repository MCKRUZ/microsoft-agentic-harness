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
        var resolvedPaths = config.PolicyPaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p))
            .Where(File.Exists)
            .ToList();

        var options = new GovernanceOptions
        {
            EnableAudit = config.EnableAudit,
            EnableMetrics = config.EnableMetrics,
            EnablePromptInjectionDetection = config.EnablePromptInjectionDetection,
            ConflictStrategy = (AgentGovernance.Policy.ConflictResolutionStrategy)(int)config.ConflictStrategy
        };

        foreach (var path in resolvedPaths)
            options.PolicyPaths.Add(path);

        var kernel = new GovernanceKernel(options);

        services.AddSingleton(kernel);
        services.AddSingleton(kernel.PolicyEngine);
        services.AddSingleton<AuditLogger>();

        services.AddSingleton<IGovernancePolicyEngine, AgtPolicyEngineAdapter>();

        // Prompt-injection detection is optional. The AGT kernel only builds an InjectionDetector when
        // GovernanceConfig.EnablePromptInjectionDetection is true, so registering kernel.InjectionDetector
        // (or the AgtPromptInjectionAdapter that requires it) unconditionally crashes composition for the
        // valid Enabled=true, EnablePromptInjectionDetection=false configuration — AddSingleton throws on a
        // null instance. When detection is off, satisfy IPromptInjectionScanner with the no-op scanner so
        // every consumer resolves and the PromptInjectionBehavior simply passes through.
        if (config.EnablePromptInjectionDetection && kernel.InjectionDetector is not null)
        {
            services.AddSingleton(kernel.InjectionDetector);
            services.AddSingleton<IPromptInjectionScanner, AgtPromptInjectionAdapter>();
        }
        else
        {
            services.AddSingleton<IPromptInjectionScanner, NoOpInjectionScanner>();
        }

        services.AddSingleton<IGovernanceAuditService, AgtAuditAdapter>();
        services.AddSingleton<IMcpSecurityScanner, McpSecurityScannerAdapter>();

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
        services.AddSingleton<ICompositeResponseSanitizer, NoOpResponseSanitizer>();

        // Data-classification seam (governance disabled): the pure evaluator plus a benign no-op
        // provider that classifies every asset as Unknown, so the dependency resolves without
        // contacting Purview or throwing.
        services.AddSingleton<IClassificationPolicyEvaluator, DefaultClassificationPolicyEvaluator>();
        services.AddSingleton<IDataClassificationProvider, NoOpDataClassificationProvider>();

        return services;
    }
}
