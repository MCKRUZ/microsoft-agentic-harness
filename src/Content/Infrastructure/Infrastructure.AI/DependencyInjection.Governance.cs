using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config;
using Infrastructure.AI.A2A;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Governance;
using Infrastructure.AI.Permissions;
using Infrastructure.AI.Resilience;
using Infrastructure.AI.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers governance services: permission system (3-phase resolution, pattern matching,
    /// safety gates, denial tracking), autonomy tier resolution, and A2A agent host.
    /// </summary>
    private static void RegisterGovernanceServices(IServiceCollection services)
    {
        // Permission system — 3-phase resolution with denial tracking
        services.AddSingleton<IPatternMatcher, GlobPatternMatcher>();
        services.AddSingleton<ISafetyGateRegistry, SafetyGateRegistry>();
        services.AddSingleton<IPermissionRuleProvider, ConfigBasedRuleProvider>();
        services.AddSingleton<IDenialTracker, InMemoryDenialTracker>();
        services.AddSingleton<IToolPermissionService, ThreePhasePermissionResolver>();

        // A2A protocol — agent-to-agent communication
        services.AddSingleton<IA2AAgentHost, A2AAgentHost>();

        // Autonomy tier resolution — reads tier from SubagentDefinition or falls back to config
        services.AddSingleton<IAutonomyTierResolver, DefaultAutonomyTierResolver>();
    }

    /// <summary>
    /// Registers escalation pipeline services: service, audit store, composite notifier,
    /// and no-op notification channel stubs.
    /// </summary>
    private static void RegisterEscalationServices(IServiceCollection services)
    {
        services.AddSingleton<IEscalationService, DefaultEscalationService>();
        services.AddSingleton<IEscalationAuditStore, JsonlEscalationAuditStore>();
        services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, DriftEscalationBridge>();
    }

    /// <summary>
    /// Registers resilience pipeline services: health monitor, capability registry,
    /// resilient provider, and conditionally the retry queue hosted service.
    /// </summary>
    private static void RegisterResilienceServices(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<PollyProviderHealthMonitor>();
        services.AddSingleton<IProviderHealthMonitor>(sp => sp.GetRequiredService<PollyProviderHealthMonitor>());
        services.AddSingleton<ProviderCapabilityRegistry>();
        services.AddSingleton<IResilientChatClientProvider, ResilientChatClientProvider>();

        if (appConfig.AI.Resilience.Enabled)
        {
            services.AddSingleton<LlmRetryQueue>();
            services.AddSingleton<ILlmRetryQueue>(sp => sp.GetRequiredService<LlmRetryQueue>());
            services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
        }
    }
}
