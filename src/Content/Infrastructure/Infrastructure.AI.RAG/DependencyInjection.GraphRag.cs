using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Domain.Common.Config;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the graph database backend and community detector services.
    /// The backend provider is selected via <c>GraphDatabaseConfig.Provider</c>
    /// using keyed DI.
    /// </summary>
    private static void AddRagGraphDatabase(IServiceCollection services, AppConfig appConfig)
    {
        var graphDbConfig = appConfig.AI.Rag.GraphDatabase;
        if (!graphDbConfig.Enabled)
            return;

        // Graph database backends — keyed by provider name
        services.AddKeyedSingleton<IGraphDatabaseBackend>("kuzu", (sp, _) =>
            new KuzuGraphBackend(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>()
                    .CurrentValue.AI.Rag.GraphDatabase.DataDirectory,
                sp.GetRequiredService<ILogger<KuzuGraphBackend>>()));

        // Default graph backend from config
        services.AddSingleton<IGraphDatabaseBackend>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.GraphDatabase.Provider;
            return sp.GetRequiredKeyedService<IGraphDatabaseBackend>(provider);
        });

        // Community detector
        services.AddSingleton<ICommunityDetector>(sp =>
            new LeidenCommunityDetector(
                sp.GetRequiredService<ILogger<LeidenCommunityDetector>>()));
    }

    /// <summary>
    /// Registers cross-session memory services: memory store and decay service.
    /// Only registered when <c>CrossSessionMemoryConfig.Enabled</c> is <c>true</c>.
    /// </summary>
    private static void AddRagCrossSessionMemory(IServiceCollection services, AppConfig appConfig)
    {
        var memoryConfig = appConfig.AI.Rag.CrossSessionMemory;
        if (!memoryConfig.Enabled)
            return;

        services.AddSingleton<ICrossSessionMemoryStore>(sp =>
            new CrossSessionMemoryStore(
                sp.GetRequiredService<IGraphDatabaseBackend>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<CrossSessionMemoryStore>>()));

        services.AddSingleton<IMemoryDecayService>(sp =>
            new MemoryDecayService(
                sp.GetRequiredService<IGraphDatabaseBackend>(),
                sp.GetRequiredService<ICrossSessionMemoryStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<MemoryDecayService>>()));

        // Background scheduler that actually runs decay+prune over time. Opt-in: without it the
        // decay service is inert (only ever invoked by tests). Resolves the decay service from a
        // fresh scope per tick so it stays valid under ValidateScopes=true.
        if (memoryConfig.DecayScheduler.Enabled)
            services.AddHostedService<GraphRag.MemoryDecayScheduler>();
    }

    /// <summary>
    /// Registers the GraphRAG knowledge graph service for entity-relationship
    /// based retrieval and community-level summarization.
    /// </summary>
    private static void AddRagGraphRag(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<IGraphRagService>(sp =>
            new ManagedCodeGraphRagService(
                sp.GetRequiredService<IGraphDatabaseBackend>(),
                sp.GetRequiredService<IModelRouter>(),
                sp.GetRequiredService<IProvenanceStamper>(),
                sp.GetRequiredService<ICommunityDetector>(),
                sp.GetRequiredService<ILogger<ManagedCodeGraphRagService>>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        // Feedback-weighted scoring (only registered when feedback enabled)
        if (appConfig.AI.Rag.GraphRag.FeedbackEnabled)
        {
            services.AddSingleton<IFeedbackWeightedScorer>(sp =>
                new FeedbackWeightedScorer(
                    sp.GetRequiredService<IFeedbackStore>(),
                    sp.GetRequiredService<IGraphDatabaseBackend>(),
                    sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                    sp.GetRequiredService<ILogger<FeedbackWeightedScorer>>()));
        }
    }
}
