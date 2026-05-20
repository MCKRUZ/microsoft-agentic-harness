using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents;
using Domain.Common.Config;
using Infrastructure.AI.RAG.Assembly;
using Infrastructure.AI.RAG.Evaluation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Ingestion;
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

/// <summary>
/// Dependency injection extensions for the RAG pipeline infrastructure.
/// Registers all ingestion, retrieval, query transformation, evaluation,
/// GraphRAG, and orchestration services.
/// </summary>
public static class DependencyInjection
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

	private static void AddRagIngestion(IServiceCollection services, AppConfig appConfig)
	{
		// Document parsing
		services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();

		// Structure extraction
		services.AddSingleton<IStructureExtractor, MarkdownStructureExtractor>();

		// Chunking strategies — keyed by strategy name
		services.AddKeyedSingleton<IChunkingService>("structure_aware", (sp, _) =>
			new StructureAwareChunker(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
		services.AddKeyedSingleton<IChunkingService>("fixed_size", (sp, _) =>
			new FixedSizeChunker(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
		services.AddKeyedSingleton<IChunkingService>("semantic", (sp, _) =>
			new SemanticChunker(
				sp.GetRequiredService<IEmbeddingService>(),
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

		// Default chunking service (resolve based on config)
		services.AddSingleton<IChunkingService>(sp =>
		{
			var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
			var strategy = config.AI.Rag.Ingestion.DefaultStrategy;
			return sp.GetRequiredKeyedService<IChunkingService>(strategy);
		});

		// Strategy resolver
		services.AddSingleton<ChunkingStrategyResolver>();

		// Contextual enrichment
		services.AddSingleton<IContextualEnricher, ContextualChunkEnricher>();

		// RAPTOR summarization
		services.AddSingleton<IRaptorSummarizer, RaptorSummarizer>();

		// Embedding service
		services.AddSingleton<IEmbeddingService, EmbeddingService>();

		// Model router (cost control)
		services.AddSingleton<IRagModelRouter, RagModelRouter>();
	}

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
				sp.GetRequiredService<IRagModelRouter>(),
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

		// Query transformers — keyed by strategy name
		services.AddKeyedSingleton<IQueryTransformer>("rag_fusion", (sp, _) =>
			new RagFusionTransformer(
				sp.GetRequiredService<IRagModelRouter>(),
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
				sp.GetRequiredService<ILogger<RagFusionTransformer>>()));

		services.AddKeyedSingleton<IQueryTransformer>("hyde", (sp, _) =>
			new HydeTransformer(
				sp.GetRequiredService<IRagModelRouter>(),
				sp.GetRequiredService<ILogger<HydeTransformer>>()));

		// Query router (orchestrates classification + transformation)
		services.AddSingleton<QueryRouter>();
	}

	/// <summary>
	/// Registers Phase 5 quality control services: CRAG evaluation, pointer expansion,
	/// citation tracking, and context assembly.
	/// </summary>
	private static void AddRagEvaluation(IServiceCollection services, AppConfig appConfig)
	{
		// CRAG evaluator — singleton (stateless, uses model router for LLM calls)
		services.AddSingleton<ICragEvaluator, CragEvaluator>();

		// Pointer expander — singleton (stateless, deduplicates per-call via local sets)
		services.AddSingleton<IPointerExpander, PointerChunkExpander>();

		// Context assembler — singleton (creates CitationTracker internally per call)
		services.AddSingleton<IRagContextAssembler, RagContextAssembler>();
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
				sp.GetRequiredService<IRagModelRouter>(),
				sp.GetRequiredService<IProvenanceStamper>(),
				sp.GetRequiredService<ICommunityDetector>(),
				sp.GetRequiredService<ILogger<ManagedCodeGraphRagService>>(),
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

		// Feedback-weighted scoring (only registered when feedback enabled)
		if (appConfig.AI.Rag.GraphRag.FeedbackEnabled)
		{
			services.AddSingleton<IFeedbackWeightedScorer>(sp =>
				new Retrieval.FeedbackWeightedScorer(
					sp.GetRequiredService<IFeedbackStore>(),
					sp.GetRequiredService<IGraphDatabaseBackend>(),
					sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
					sp.GetRequiredService<ILogger<Retrieval.FeedbackWeightedScorer>>()));
		}
	}

	/// <summary>
	/// Registers the complexity classifier and retrieval decision gate for
	/// cost-aware query routing (Phase A — Adaptive Routing).
	/// </summary>
	private static void AddRagComplexityRouting(IServiceCollection services)
	{
		services.AddSingleton<IQueryComplexityClassifier, QueryComplexityClassifier>();
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
	/// Registers Phase B answer faithfulness evaluation services.
	/// </summary>
	private static void AddRagFaithfulness(IServiceCollection services, AppConfig appConfig)
	{
		services.AddSingleton<IAnswerFaithfulnessEvaluator, AnswerFaithfulnessEvaluator>();
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
				sp.GetService<IQueryComplexityClassifier>(),
				sp.GetService<IRetrievalCostTracker>(),
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
				sp.GetRequiredService<ILogger<RagOrchestrator>>(),
				sp.GetService<IRetrievalDecisionGate>(),
				sp.GetService<IIterativeRetriever>(),
				sp.GetService<IAnswerFaithfulnessEvaluator>()));
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
	/// Registers the Ragas-inspired <see cref="IRetrievalQualityEvaluator"/> for evaluating
	/// retrieval quality via LLM judges. Used by CI/CD quality gate tests and runtime
	/// quality monitoring.
	/// </summary>
	private static void AddRagQualityGates(IServiceCollection services, AppConfig appConfig)
	{
		services.AddSingleton<IRetrievalQualityEvaluator>(sp =>
			new RetrievalQualityEvaluator(
				sp.GetRequiredService<IRagModelRouter>(),
				sp.GetRequiredService<ILogger<RetrievalQualityEvaluator>>()));
	}

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
	/// <see cref="SafeSqlQueryExecutor"/> requires a <see cref="System.Data.Common.DbConnection"/>
	/// registered in DI by the consuming application. If no connection is registered the
	/// "sql_database" source will throw at resolution time, not at startup.
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

		// DbConnection is opt-in — the consuming app must register one.
		// SafeSqlQueryExecutor is resolved lazily so a missing DbConnection only fails
		// at first retrieval, not at startup.
		services.AddSingleton<ISqlQueryExecutor>(sp =>
			new SafeSqlQueryExecutor(
				sp.GetRequiredService<System.Data.Common.DbConnection>(),
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
