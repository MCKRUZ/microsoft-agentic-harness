using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Domain.Common.Config;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG;

/// <summary>
/// Dependency injection extensions for the RAG pipeline infrastructure.
/// Registers all ingestion, retrieval, query transformation, evaluation,
/// GraphRAG, and orchestration services.
/// </summary>
public static partial class DependencyInjection
{
    /// <summary>
    /// Adds all RAG pipeline infrastructure services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="appConfig">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRagDependencies(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        AddRagIngestion(services, appConfig);
        AddRagRetrieval(services, appConfig);
        AddRagQueryTransform(services, appConfig);
        AddRagEvaluation(services, appConfig);
        AddRagGraphDatabase(services, appConfig);
        AddRagCrossSessionMemory(services, appConfig);
        AddRagGraphRag(services, appConfig);
        AddRagComplexityRouting(services);
        AddRagMultiHop(services, appConfig);
        AddRagFaithfulness(services, appConfig);
        AddRagOrchestration(services, appConfig);
        AddRagMultiSource(services, appConfig);
        AddRagQualityGates(services, appConfig);
        AddRagWebSearch(services, appConfig);
        AddRagSqlDatabase(services, appConfig);

        return services;
    }

    /// <summary>
    /// Registers the top-level RAG orchestrator that coordinates all pipeline
    /// stages (classify, retrieve, rerank, evaluate, assemble) into a single
    /// <see cref="IRagOrchestrator.SearchAsync"/> entry point.
    /// Phase B optional services (iterative retriever, faithfulness evaluator)
    /// are resolved via <see cref="ServiceProviderServiceExtensions.GetService{T}"/>
    /// and remain null when not registered.
    /// </summary>
    private static void AddRagOrchestration(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<IRagOrchestrator>(sp =>
            new RagOrchestrator(
                sp.GetRequiredService<IHybridRetriever>(),
                sp.GetRequiredService<IReranker>(),
                sp.GetRequiredService<ICragEvaluator>(),
                sp.GetRequiredService<IRagContextAssembler>(),
                sp.GetRequiredService<IGraphRagService>(),
                sp.GetService<IFeedbackWeightedScorer>(),
                sp.GetRequiredService<QueryRouter>(),
                sp.GetService<IMultiSourceOrchestrator>(),
                sp.GetService<ITaskComplexityClassifier>(),
                sp.GetService<IRetrievalCostTracker>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<RagOrchestrator>>(),
                sp.GetService<IRetrievalDecisionGate>(),
                sp.GetService<IIterativeRetriever>(),
                sp.GetService<IAnswerFaithfulnessEvaluator>(),
                sp.GetKeyedService<IRetrievalSource>("web_search")));
    }
}
