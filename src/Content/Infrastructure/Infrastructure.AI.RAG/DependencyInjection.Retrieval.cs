using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Application.Common.Interfaces.Data;
using Azure;
using Azure.Search.Documents;
using Domain.Common.Config;
using Infrastructure.AI.RAG.Evaluation;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.SqlDatabase;
using Infrastructure.AI.RAG.WebSearch;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG;

public static partial class DependencyInjection
{
    private static void AddRagRetrieval(IServiceCollection services, AppConfig appConfig)
    {
        // Vector stores — keyed by provider name
        services.AddKeyedSingleton<IVectorStore>("azure_ai_search", (sp, _) =>
            new AzureAISearchVectorStore(
                BuildSearchClient(sp),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<AzureAISearchVectorStore>>()));

        services.AddKeyedSingleton<IVectorStore>("faiss", (sp, _) =>
            new FaissVectorStore(
                sp.GetRequiredService<ILogger<FaissVectorStore>>()));

        // Default vector store from config
        services.AddSingleton<IVectorStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.VectorStore.Provider;
            return sp.GetRequiredKeyedService<IVectorStore>(provider);
        });

        // BM25 stores — keyed by provider name
        services.AddKeyedSingleton<IBm25Store>("azure_ai_search", (sp, _) =>
            new AzureAISearchBm25Store(
                BuildSearchClient(sp),
                sp.GetRequiredService<ILogger<AzureAISearchBm25Store>>()));

        services.AddKeyedSingleton<IBm25Store>("faiss", (sp, _) =>
            new SqliteFts5Store(
                null,
                sp.GetRequiredService<ILogger<SqliteFts5Store>>()));

        // Default BM25 store from config
        services.AddSingleton<IBm25Store>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.VectorStore.Provider;
            var key = provider == "azure_ai_search" ? "azure_ai_search" : "faiss";
            return sp.GetRequiredKeyedService<IBm25Store>(key);
        });

        // Hybrid retriever
        services.AddSingleton<IHybridRetriever, HybridRetriever>();

        // Rerankers — keyed by strategy name
        services.AddKeyedSingleton<IReranker>("azure_semantic", (sp, _) =>
            new AzureSemanticReranker(
                BuildSearchClient(sp),
                sp.GetRequiredService<ILogger<AzureSemanticReranker>>()));

        services.AddKeyedSingleton<IReranker>("cross_encoder", (sp, _) =>
            new CrossEncoderReranker(
                sp.GetRequiredService<IModelRouter>(),
                sp.GetRequiredService<ILogger<CrossEncoderReranker>>()));

        services.AddKeyedSingleton<IReranker>("none", (_, _) =>
            new NoOpReranker());

        // Default reranker from config
        services.AddSingleton<IReranker>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            return sp.GetRequiredKeyedService<IReranker>(config.AI.Rag.Reranker.Strategy);
        });

        // Factory for dynamic provider resolution
        services.AddSingleton<VectorStoreFactory>();
    }

    private static void AddRagQueryTransform(IServiceCollection services, AppConfig appConfig)
    {
        // Query classifier
        services.AddSingleton<IQueryClassifier, LlmQueryClassifier>();

        // Eval probe exposing the query-type router to the routing-accuracy scorecard.
        services.AddSingleton<IRouterEvalProbe, QueryTypeRouterProbe>();

        // Query transformers — keyed by strategy name
        services.AddKeyedSingleton<IQueryTransformer>("rag_fusion", (sp, _) =>
            new RagFusionTransformer(
                sp.GetRequiredService<IModelRouter>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<RagFusionTransformer>>()));

        services.AddKeyedSingleton<IQueryTransformer>("hyde", (sp, _) =>
            new HydeTransformer(
                sp.GetRequiredService<IModelRouter>(),
                sp.GetRequiredService<ILogger<HydeTransformer>>()));

        // Query router (orchestrates classification + transformation)
        services.AddSingleton<QueryRouter>();
    }

    /// <summary>
    /// Registers the complexity classifier and retrieval decision gate for
    /// cost-aware query routing (Phase A — Adaptive Routing).
    /// </summary>
    private static void AddRagComplexityRouting(IServiceCollection services)
    {
        services.AddSingleton<IRetrievalDecisionGate, RetrievalDecisionGate>();
    }

    /// <summary>
    /// Registers Phase B multi-hop iterative retrieval services: query decomposer,
    /// sufficiency evaluator, and iterative retriever.
    /// </summary>
    private static void AddRagMultiHop(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<IQueryDecomposer, QueryDecomposer>();
        services.AddSingleton<ISufficiencyEvaluator, SufficiencyEvaluator>();
        services.AddSingleton<IIterativeRetriever, IterativeRetriever>();
    }

    /// <summary>
    /// Registers multi-source orchestration services: the <see cref="IMultiSourceOrchestrator"/>
    /// for parallel fan-out across pluggable <see cref="IRetrievalSource"/> implementations
    /// resolved by key from DI, and the <see cref="IRetrievalCostTracker"/> for per-execution
    /// token accounting. Also registers the vector and graph source adapters keyed as
    /// "vector" and "graph" respectively.
    /// </summary>
    private static void AddRagMultiSource(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<IRetrievalCostTracker, RetrievalCostTracker>();

        // Adapter: existing IHybridRetriever → IRetrievalSource
        services.AddKeyedSingleton<IRetrievalSource>("vector", (sp, _) =>
            new VectorRetrievalSource(sp.GetRequiredService<IHybridRetriever>()));

        // Adapter: existing IGraphRagService → IRetrievalSource
        services.AddKeyedSingleton<IRetrievalSource>("graph", (sp, _) =>
            new GraphRetrievalSource(sp.GetRequiredService<IGraphRagService>()));

        // Orchestrator resolves IRetrievalSource by key from the container
        services.AddSingleton<IMultiSourceOrchestrator>(sp =>
            new MultiSourceOrchestrator(
                sp,
                sp.GetRequiredService<IRetrievalCostTracker>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<MultiSourceOrchestrator>>()));
    }

    /// <summary>
    /// Registers web search retrieval services: the Bing provider and the
    /// <see cref="IRetrievalSource"/> adapter keyed as "web_search".
    /// The named <c>BingWebSearch</c> <see cref="HttpClient"/> must have the
    /// <c>Ocp-Apim-Subscription-Key</c> header set by the Presentation layer
    /// via User Secrets or Key Vault before calling this method.
    /// </summary>
    private static void AddRagWebSearch(IServiceCollection services, AppConfig appConfig)
    {
        // Named HttpClient for Bing — BaseAddress is fixed; subscription key header
        // is set by the Presentation layer's DI composition via IHttpClientFactory.
        services.AddHttpClient("BingWebSearch", (sp, client) =>
        {
            client.BaseAddress = new Uri("https://api.bing.microsoft.com/");
        });

        // Bing provider — keyed by provider name
        services.AddKeyedSingleton<IWebSearchProvider>("bing", (sp, _) =>
            new BingWebSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("BingWebSearch"),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<BingWebSearchProvider>>()));

        // Default provider from config
        services.AddSingleton<IWebSearchProvider>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var provider = config.AI.Rag.WebSearch.Provider;
            return sp.GetRequiredKeyedService<IWebSearchProvider>(provider);
        });

        // IRetrievalSource adapter — keyed as "web_search" for multi-source orchestration
        services.AddKeyedSingleton<IRetrievalSource>("web_search", (sp, _) =>
            new WebSearchRetrievalSource(sp.GetRequiredService<IWebSearchProvider>()));
    }

    /// <summary>
    /// Registers SQL database retrieval services: template store, safe executor,
    /// template matcher, text-to-SQL generator, and the <see cref="IRetrievalSource"/>
    /// adapter keyed as "sql_database". Only registered when
    /// <see cref="SqlDatabaseConfig.Enabled"/> is <c>true</c>.
    /// <para>
    /// <see cref="SafeSqlQueryExecutor"/> requires an <see cref="ISqlConnectionFactory"/>
    /// registered in DI by the consuming application (Infrastructure.Common provides a default
    /// <c>SqlConnectionFactory</c>). The executor creates, opens, and disposes a fresh connection
    /// per call rather than sharing one. If no factory is registered the "sql_database" source
    /// will throw at resolution time, not at startup.
    /// </para>
    /// </summary>
    private static void AddRagSqlDatabase(IServiceCollection services, AppConfig appConfig)
    {
        var config = appConfig.AI?.Rag?.SqlDatabase;
        if (config is null || !config.Enabled) return;

        services.AddSingleton<ISqlQueryTemplateStore>(sp =>
            new JsonSqlQueryTemplateStore(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        services.AddSingleton<SqlQueryTemplateMatcher>(sp =>
            new SqlQueryTemplateMatcher(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        services.AddSingleton<TextToSqlGenerator>(sp =>
            new TextToSqlGenerator(sp.GetRequiredService<IChatClient>()));

        // ISqlConnectionFactory is opt-in — the consuming app must register one.
        // SafeSqlQueryExecutor is resolved lazily so a missing factory only fails
        // at first retrieval, not at startup.
        services.AddSingleton<ISqlQueryExecutor>(sp =>
            new SafeSqlQueryExecutor(
                sp.GetRequiredService<ISqlConnectionFactory>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<SafeSqlQueryExecutor>>()));

        services.AddKeyedSingleton<IRetrievalSource>("sql_database", (sp, _) =>
            new SqlDatabaseRetrievalSource(
                sp.GetRequiredService<ISqlQueryTemplateStore>(),
                sp.GetRequiredService<ISqlQueryExecutor>(),
                sp.GetRequiredService<SqlQueryTemplateMatcher>(),
                sp.GetRequiredService<TextToSqlGenerator>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<SqlDatabaseRetrievalSource>>()));
    }

    /// <summary>
    /// Builds a <see cref="SearchClient"/> from the vector store configuration.
    /// Falls back to a placeholder endpoint when not configured, allowing DI
    /// resolution to succeed for non-Azure providers.
    /// </summary>
    private static SearchClient BuildSearchClient(IServiceProvider sp)
    {
        var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
        var vsConfig = config.AI.Rag.VectorStore;

        var endpoint = vsConfig.IsConfigured
            ? new Uri(vsConfig.Endpoint!)
            : new Uri("https://not-configured.search.windows.net");

        var credential = !string.IsNullOrWhiteSpace(vsConfig.ApiKey)
            ? new AzureKeyCredential(vsConfig.ApiKey)
            : new AzureKeyCredential("not-configured");

        return new SearchClient(endpoint, vsConfig.IndexName, credential);
    }
}
