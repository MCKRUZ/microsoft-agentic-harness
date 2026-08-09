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

        // PR-4: graded-autonomy startup validator — refuses to boot when
        // GradedAutonomy.Enabled is true and the config is internally
        // inconsistent (Critical→AutoApprove, Prod→High→AutoApprove, per-skill
        // tier looser than baseline, etc.). No-ops when GradedAutonomy is off.
        services.AddHostedService<AutonomyConfigValidator>();
    }

    /// <summary>
    /// Registers escalation pipeline services: service (also exposed as the operator-facing
    /// <see cref="IEscalationReconciler"/>), audit store, composite notifier, no-op
    /// notification channel stubs, and the startup rehydration step for durable escalation
    /// state (a no-op while durability is disabled).
    /// </summary>
    private static void RegisterEscalationServices(IServiceCollection services)
    {
        // Concrete registration with interface forwards so IEscalationService and
        // IEscalationReconciler resolve to the SAME singleton — the reconciler must see the
        // exact in-memory state the service accumulates.
        services.AddSingleton<DefaultEscalationService>();
        services.AddSingleton<IEscalationService>(sp => sp.GetRequiredService<DefaultEscalationService>());
        services.AddSingleton<IEscalationReconciler>(sp => sp.GetRequiredService<DefaultEscalationService>());

        // Restores durably persisted pending escalations at host start. With durable state
        // disabled the Null state store yields nothing and this is a silent no-op.
        services.AddHostedService<EscalationStateRehydrationService>();

        // Runs reconciliation in production — the ONLY non-test trigger for the recovery path.
        // Registered AFTER rehydration so hosted-service start order populates the active set
        // before the first pass runs (the pass distinguishes in-memory-recoverable records from
        // durable-only ones by consulting that set). The pass itself runs on EVERY host: the
        // stuck state it recovers is caused by an audit-store failure, not by durable state, so
        // it occurs with the durability toggles off. Only the retention prune is toggle-gated.
        services.AddHostedService<EscalationReconciliationService>();

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
        services.AddSingleton<IProviderErrorClassifier, DefaultProviderErrorClassifier>();
        services.AddSingleton<IResilientChatClientProvider, ResilientChatClientProvider>();

        if (appConfig.AI.Resilience.Enabled)
        {
            services.AddSingleton<LlmRetryQueue>();
            services.AddSingleton<ILlmRetryQueue>(sp => sp.GetRequiredService<LlmRetryQueue>());
            services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
        }
    }
}
