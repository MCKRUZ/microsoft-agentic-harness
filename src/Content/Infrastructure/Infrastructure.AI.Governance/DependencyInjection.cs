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
        services.AddSingleton(kernel.InjectionDetector);
        services.AddSingleton<AuditLogger>();

        services.AddSingleton<IGovernancePolicyEngine, AgtPolicyEngineAdapter>();
        services.AddSingleton<IPromptInjectionScanner, AgtPromptInjectionAdapter>();
        services.AddSingleton<IGovernanceAuditService, AgtAuditAdapter>();
        services.AddSingleton<IMcpSecurityScanner, McpSecurityScannerAdapter>();

        services.AddSingleton<IResponseSanitizer, CredentialRedactor>();
        services.AddSingleton<IResponseSanitizer, ResponseInjectionScrubber>();
        services.AddSingleton<IResponseSanitizer, ExfiltrationUrlDetector>();
        services.AddSingleton<ICompositeResponseSanitizer, CompositeResponseSanitizer>();

        // Data-classification seam. The policy evaluator is pure; the provider is the real Graph-backed
        // Information Protection client when wired, else the fail-fast default.
        AddDataClassificationProvider(services, config.DataClassification);

        return services;
    }

    /// <summary>
    /// Registers the data-classification policy evaluator and the appropriate
    /// <see cref="IDataClassificationProvider"/>. Wires the real Graph-backed
    /// <see cref="GraphSensitivityLabelClient"/> (behind a TTL cache) only when classification is enabled
    /// and the Information Protection provider is switched on; otherwise keeps the fail-fast default so an
    /// enabled-but-unconfigured gate fails loudly rather than silently allowing everything.
    /// </summary>
    private static void AddDataClassificationProvider(IServiceCollection services, DataClassificationConfig config)
    {
        services.AddSingleton<IClassificationPolicyEvaluator, DefaultClassificationPolicyEvaluator>();

        var ip = config.InformationProtection;
        if (config.Mode == ClassificationEnforcementMode.Off || !ip.Enabled)
        {
            services.AddSingleton<IDataClassificationProvider, NotConfiguredDataClassificationProvider>();
            return;
        }

        services.AddHttpClient(GraphSensitivityLabelClient.HttpClientName);

        services.AddSingleton<IDataClassificationProvider>(sp =>
        {
            var timeProvider = sp.GetRequiredService<TimeProvider>();

            // Azure.Identity caches and refreshes the token in-process, so building the credential once
            // for the singleton is correct; it is created lazily here so a credential-config error
            // surfaces when the provider is first resolved rather than during the DI graph build.
            var credential = AzureCredentialFactory.CreateTokenCredential(ip.Auth);

            var inner = new GraphSensitivityLabelClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                credential,
                ip,
                timeProvider,
                sp.GetRequiredService<ILogger<GraphSensitivityLabelClient>>());

            return new CachedDataClassificationProvider(
                inner,
                timeProvider,
                config.ResultCacheTtl,
                sp.GetRequiredService<ILogger<CachedDataClassificationProvider>>());
        });
    }

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
