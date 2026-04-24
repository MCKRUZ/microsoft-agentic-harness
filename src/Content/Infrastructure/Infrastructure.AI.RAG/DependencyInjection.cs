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
		AddRagGraphRag(services, appConfig);
		AddRagOrchestration(services, appConfig);

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
				sp.GetRequiredService<IKnowledgeGraphStore>(),
				sp.GetRequiredService<IRagModelRouter>(),
				sp.GetRequiredService<IProvenanceStamper>(),
				sp.GetRequiredService<ILogger<ManagedCodeGraphRagService>>(),
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

		// Feedback-weighted scoring (only registered when feedback enabled)
		if (appConfig.AI.Rag.GraphRag.FeedbackEnabled)
		{
			services.AddSingleton<IFeedbackWeightedScorer>(sp =>
				new Retrieval.FeedbackWeightedScorer(
					sp.GetRequiredService<IFeedbackStore>(),
					sp.GetRequiredService<IKnowledgeGraphStore>(),
					sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
					sp.GetRequiredService<ILogger<Retrieval.FeedbackWeightedScorer>>()));
		}
	}

	/// <summary>
	/// Registers the top-level RAG orchestrator that coordinates all pipeline
	/// stages (classify, retrieve, rerank, evaluate, assemble) into a single
	/// <see cref="IRagOrchestrator.SearchAsync"/> entry point.
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
				sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
				sp.GetRequiredService<ILogger<RagOrchestrator>>()));
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
