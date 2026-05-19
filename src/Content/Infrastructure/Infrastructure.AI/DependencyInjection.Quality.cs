using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Learnings;
using Domain.Common.Config;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers drift detection pipeline: scorer, baseline store, audit, notifier, EWMA state,
    /// and the main detection service.
    /// </summary>
    private static void RegisterDriftDetectionServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IDriftScorer>("ewma", (sp, _) =>
            new EwmaDriftScorer(
                sp.GetRequiredService<IEwmaStateStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<EwmaDriftScorer>>()));

        services.AddKeyedSingleton<IDriftBaselineStore>("graph", (sp, _) =>
            new GraphDriftBaselineStore(
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<ILogger<GraphDriftBaselineStore>>()));

        services.AddKeyedSingleton<IDriftBaselineStore>("in_memory", (_, _) =>
            new InMemoryDriftBaselineStore());

        // Default to graph — drift baselines require persistent storage for EWMA continuity
        services.AddSingleton<IDriftBaselineStore>(sp =>
            sp.GetRequiredKeyedService<IDriftBaselineStore>("graph"));

        services.AddSingleton<IDriftAuditStore>(sp =>
            new JsonlDriftAuditStore(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<JsonlDriftAuditStore>>()));

        services.AddSingleton<IDriftNotifier, CompositeDriftNotifier>();

        services.AddSingleton<IEwmaStateStore>(sp =>
            new GraphEwmaStateStore(
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<ILogger<GraphEwmaStateStore>>()));

        services.AddSingleton<IDriftDetectionService>(sp =>
            new DefaultDriftDetectionService(
                sp.GetRequiredKeyedService<IDriftScorer>("ewma"),
                sp.GetRequiredService<IDriftBaselineStore>(),
                sp.GetRequiredService<IDriftAuditStore>(),
                sp.GetRequiredService<IDriftNotifier>(),
                sp.GetRequiredService<IEscalationService>(),
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<DefaultDriftDetectionService>>()));
    }

    /// <summary>
    /// Registers learnings subsystem: decay service, drift bridge, and conditional
    /// pruning background service.
    /// </summary>
    private static void RegisterLearningsServices(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<ILearningDecayService>(sp =>
            new DefaultLearningDecayService(
                sp.GetRequiredService<ILearningsStore>(),
                sp.GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.Learnings.LearningsConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<DefaultLearningDecayService>>()));

        services.AddSingleton<ILearningsDriftBridge, LearningsDriftBridge>();

        if (appConfig.AI.Learnings.Enabled)
        {
            services.AddSingleton<LearningsPruningBackgroundService>();
            services.AddHostedService(sp => sp.GetRequiredService<LearningsPruningBackgroundService>());
        }
    }
}
