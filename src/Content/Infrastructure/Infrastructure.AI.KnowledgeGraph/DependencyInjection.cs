using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Neo4j;
using Infrastructure.AI.KnowledgeGraph.PostgreSql;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Provenance;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.DependencyInjection;
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

        // Default resolution from config
        services.AddSingleton<IKnowledgeGraphStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.GraphRag.GraphProvider;
            return sp.GetRequiredKeyedService<IKnowledgeGraphStore>(provider);
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
                sp.GetRequiredService<Application.AI.Common.Interfaces.RAG.IRagModelRouter>(),
                sp.GetRequiredService<ILogger<LlmFeedbackDetector>>()));

        // Cross-session knowledge persistence (scoped per request/session)
        services.AddScoped<ISessionKnowledgeCache, InMemorySessionCache>();
        services.AddScoped<IKnowledgeMemory>(sp =>
            new KnowledgeMemoryService(
                sp.GetRequiredService<ISessionKnowledgeCache>(),
                sp.GetRequiredService<IKnowledgeGraphStore>(),
                sp.GetService<IFeedbackDetector>(),
                sp.GetService<IFeedbackStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<KnowledgeMemoryService>>()));

        // Knowledge scope accessor (scoped per request)
        services.AddScoped<KnowledgeScopeAccessor>();
        services.AddScoped<IKnowledgeScope>(sp => sp.GetRequiredService<KnowledgeScopeAccessor>());

        // Multi-tenant isolation (conditional decorator)
        services.AddSingleton<IKnowledgeScopeValidator>(sp =>
            new KnowledgeScopeValidator(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        return services;
    }
}
