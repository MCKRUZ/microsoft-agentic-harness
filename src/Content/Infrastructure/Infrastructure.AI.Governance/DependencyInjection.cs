using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Policy;
using AgentGovernance.Security;
using Application.AI.Common.Interfaces.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance.Adapters;
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

        return services;
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

        return services;
    }
}
