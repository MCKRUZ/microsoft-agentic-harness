using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Infrastructure.AI.KnowledgeGraph.Audit;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Neo4j;
using Infrastructure.AI.KnowledgeGraph.PostgreSql;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Provenance;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Infrastructure.AI.KnowledgeGraph.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph;

/// <summary>
/// Dependency injection extensions for the knowledge graph infrastructure.
/// Registers graph store backends with keyed DI for provider selection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds knowledge graph infrastructure services to the service collection.
    /// Registers <see cref="IKnowledgeGraphStore"/> implementations keyed by provider
    /// name (<c>"in_memory"</c>, <c>"postgresql"</c>, <c>"neo4j"</c>) and resolves
    /// the default from <c>AppConfig.AI.Rag.GraphRag.GraphProvider</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="appConfig">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddKnowledgeGraphDependencies(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        services.AddKeyedSingleton<IKnowledgeGraphStore>("in_memory", (sp, _) =>
            new InMemoryGraphStore(
                sp.GetRequiredService<ILogger<InMemoryGraphStore>>()));

        services.AddKeyedSingleton<IKnowledgeGraphStore>("postgresql", (sp, _) =>
            new PostgreSqlGraphStore(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<PostgreSqlGraphStore>>()));

        services.AddKeyedSingleton<IKnowledgeGraphStore>("neo4j", (sp, _) =>
            new Neo4jGraphStore(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<Neo4jGraphStore>>()));

        // Backward compat: "managed_code" maps to "in_memory"
        services.AddKeyedSingleton<IKnowledgeGraphStore>("managed_code", (sp, _) =>
            sp.GetRequiredKeyedService<IKnowledgeGraphStore>("in_memory"));

        // The decorator chain below resolves the caller's scope per-op via IAmbientRequestScope.
        // The composition root registers it from Application.AI.Common; TryAdd keeps this layer's
        // graph store self-sufficient when wired in isolation (e.g. infra-only DI tests), deferring
        // to the canonical registration when both run.
        services.TryAddSingleton<
            Application.AI.Common.Interfaces.IAmbientRequestScope,
            Application.AI.Common.Services.AmbientRequestScope>();

        // Default resolution from config, wrapped in the tenant-isolation + compliance decorator
        // chain: backend -> TenantIsolatedGraphStore (per-record owner filtering) ->
        // ComplianceAwareGraphStore (temporal stamping, expiry filtering, audit), i.e. Compliance is
        // outermost. This order is deliberate: Tenant sits closest to the backend so it (a) reads a
        // node's owner directly without triggering Compliance's Recall audit, (b) sees the owner
        // already stamped by Compliance on writes so write-side enforcement is meaningful, and
        // (c) lets Compliance emit Recall audits only for nodes the caller is actually allowed to see.
        // Both decorators are singletons that resolve the caller's scope per-op via
        // IAmbientRequestScope, so they neither capture a scoped dependency nor force the store to
        // become scoped (GraphLearningsStore and RetentionEnforcementService are singletons that
        // inject IKnowledgeGraphStore).
        services.AddSingleton<IKnowledgeGraphStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.GraphRag.GraphProvider;
            IKnowledgeGraphStore store = sp.GetRequiredKeyedService<IKnowledgeGraphStore>(provider);

            if (config.AI.Rag.GraphRag.MultiTenantIsolation)
            {
                store = new TenantIsolatedGraphStore(
                    store,
                    sp.GetRequiredService<Application.AI.Common.Interfaces.IAmbientRequestScope>(),
                    sp.GetRequiredService<IKnowledgeScopeValidator>(),
                    sp.GetRequiredService<ILogger<TenantIsolatedGraphStore>>());
            }

            if (config.AI.Rag.GraphRag.ComplianceEnabled)
            {
                store = new ComplianceAwareGraphStore(
                    store,
                    sp.GetRequiredService<IMemoryAuditSink>(),
                    sp.GetRequiredService<Application.AI.Common.Interfaces.IAmbientRequestScope>(),
                    sp.GetRequiredService<IRetentionPolicyProvider>(),
                    sp.GetService<TimeProvider>() ?? TimeProvider.System,
                    sp.GetRequiredService<ILogger<ComplianceAwareGraphStore>>());
            }

            return store;
        });

        // Provenance stamping
        services.AddSingleton<IProvenanceStamper>(sp =>
            new DefaultProvenanceStamper(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // Feedback services
        services.AddSingleton<IFeedbackStore>(sp =>
            new GraphFeedbackStore(
                sp.GetRequiredService<ILogger<GraphFeedbackStore>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddSingleton<IFeedbackDetector>(sp =>
            new LlmFeedbackDetector(
                sp.GetRequiredService<Application.AI.Common.Interfaces.Routing.IModelRouter>(),
                sp.GetRequiredService<ILogger<LlmFeedbackDetector>>()));

        // Cross-session knowledge persistence (scoped per request/session)
        services.AddScoped<ISessionKnowledgeCache, InMemorySessionCache>();
        services.AddScoped<IKnowledgeMemory>(sp =>
            new KnowledgeMemoryService(
                sp.GetRequiredService<ISessionKnowledgeCache>(),
                sp.GetRequiredService<IKnowledgeGraphStore>(),
                sp.GetRequiredService<IKnowledgeScope>(),
                sp.GetService<IFeedbackDetector>(),
                sp.GetService<IFeedbackStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<KnowledgeMemoryService>>()));

        // Conversation-to-Knowledge Bridge — LLM-based fact extraction from agent turns
        services.AddTransient<IConversationFactExtractor, ConversationFactExtractor>();

        // Knowledge scope accessor (scoped per request). IKnowledgeScope (read) and
        // IKnowledgeScopeWriter (set-at-entry-point) resolve to the SAME instance, so a scope
        // set by host middleware / a hub filter is observed by every consumer in that request.
        services.AddScoped<KnowledgeScopeAccessor>();
        services.AddScoped<IKnowledgeScope>(sp => sp.GetRequiredService<KnowledgeScopeAccessor>());
        services.AddScoped<IKnowledgeScopeWriter>(sp => sp.GetRequiredService<KnowledgeScopeAccessor>());

        // Multi-tenant isolation (conditional decorator)
        services.AddSingleton<IKnowledgeScopeValidator>(sp =>
            new KnowledgeScopeValidator(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        // --- Compliance Layer ---

        // Audit sinks (keyed DI)
        services.AddKeyedSingleton<IMemoryAuditSink>("no_op", (_, _) =>
            new NoOpAuditSink());
        services.AddKeyedSingleton<IMemoryAuditSink>("structured_logging", (sp, _) =>
            new StructuredLoggingAuditSink(
                sp.GetRequiredService<ILogger<StructuredLoggingAuditSink>>()));
        services.AddSingleton<IMemoryAuditSink>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var key = config.AI.Rag.GraphRag.ComplianceEnabled
                ? config.AI.Rag.GraphRag.AuditSinkProvider
                : "no_op";
            return sp.GetRequiredKeyedService<IMemoryAuditSink>(key);
        });

        // Retention policy provider
        services.AddSingleton<IRetentionPolicyProvider>(sp =>
            new ConfigRetentionPolicyProvider(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        // Erasure orchestrator
        services.AddScoped<IErasureOrchestrator>(sp =>
            new DefaultErasureOrchestrator(
                sp.GetRequiredService<IKnowledgeGraphStore>(),
                sp.GetRequiredService<IFeedbackStore>(),
                sp.GetService<IVectorStore>(),
                sp.GetRequiredService<IMemoryAuditSink>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<DefaultErasureOrchestrator>>()));

        // Retention enforcement background service
        if (appConfig.AI.Rag.GraphRag.ComplianceEnabled &&
            appConfig.AI.Rag.GraphRag.RetentionEnforcementInterval > TimeSpan.Zero)
        {
            services.AddHostedService<RetentionEnforcementService>();
        }

        // --- Procedural Memory ---

        if (appConfig.AI.Rag.GraphRag.SkillEffectivenessEnabled)
        {
            services.AddScoped<ISkillEffectivenessTracker>(sp =>
                new GraphSkillEffectivenessTracker(
                    sp.GetRequiredService<IKnowledgeGraphStore>(),
                    sp.GetRequiredService<ILogger<GraphSkillEffectivenessTracker>>()));
        }

        if (appConfig.AI.Rag.GraphRag.SkillAmendmentsEnabled)
        {
            services.AddScoped<ISkillAmendmentProvider>(sp =>
                new GraphSkillAmendmentProvider(
                    sp.GetRequiredService<IKnowledgeGraphStore>(),
                    sp.GetRequiredService<ILogger<GraphSkillAmendmentProvider>>()));
        }

        // --- Learnings Store (keyed DI) ---

        services.AddKeyedSingleton<ILearningsStore>("graph", (sp, _) =>
            new Learnings.GraphLearningsStore(
                sp.GetRequiredService<IKnowledgeGraphStore>(),
                sp.GetRequiredService<ILogger<Learnings.GraphLearningsStore>>()));

        services.AddKeyedSingleton<ILearningsStore>("in_memory", (_, _) =>
            new Learnings.InMemoryLearningsStore());

        var learningsProvider = appConfig.AI.Learnings.StoreProvider;
        services.AddSingleton<ILearningsStore>(sp =>
            sp.GetRequiredKeyedService<ILearningsStore>(learningsProvider));

        return services;
    }
}
