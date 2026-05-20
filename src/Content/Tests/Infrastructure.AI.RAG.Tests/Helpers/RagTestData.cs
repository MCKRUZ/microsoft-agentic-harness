using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.RAG.Tests.Helpers;

internal static class RagTestData
{
    public static DocumentChunk CreateChunk(
        string id = "chunk-1",
        string content = "Test content for the document chunk.",
        string sectionPath = "Section > Subsection",
        string documentId = "doc-1",
        string? parentSectionId = null,
        IReadOnlyList<string>? siblingChunkIds = null) =>
        new()
        {
            Id = id,
            DocumentId = documentId,
            SectionPath = sectionPath,
            Content = content,
            Tokens = content.Length / 4,
            Metadata = new ChunkMetadata
            {
                SourceUri = new Uri($"file:///docs/{documentId}.md"),
                CreatedAt = DateTimeOffset.UtcNow,
                ParentSectionId = parentSectionId,
                SiblingChunkIds = siblingChunkIds ?? []
            }
        };

    public static RetrievalResult CreateRetrievalResult(
        string id = "chunk-1",
        string content = "Retrieved content.",
        double denseScore = 0.9,
        double sparseScore = 0.3,
        double fusedScore = 0.85) =>
        new()
        {
            Chunk = CreateChunk(id, content),
            DenseScore = denseScore,
            SparseScore = sparseScore,
            FusedScore = fusedScore
        };

    public static RerankedResult CreateRerankedResult(
        string id = "chunk-1",
        string content = "Reranked content.",
        double rerankScore = 0.85,
        int originalRank = 1,
        int rerankRank = 1) =>
        new()
        {
            RetrievalResult = CreateRetrievalResult(id, content),
            RerankScore = rerankScore,
            OriginalRank = originalRank,
            RerankRank = rerankRank
        };

    public static IReadOnlyList<RetrievalResult> CreateRetrievalResults(int count)
    {
        var results = new List<RetrievalResult>();
        for (var i = 0; i < count; i++)
        {
            var score = 1.0 - (i * 0.1);
            results.Add(CreateRetrievalResult(
                id: $"chunk-{i + 1}",
                content: $"Content for chunk {i + 1} with enough text to be meaningful.",
                denseScore: score,
                sparseScore: score * 0.5,
                fusedScore: score * 0.9));
        }
        return results;
    }

    public static IReadOnlyList<RerankedResult> CreateRerankedResults(int count)
    {
        var results = new List<RerankedResult>();
        for (var i = 0; i < count; i++)
        {
            var score = 1.0 - (i * 0.1);
            results.Add(CreateRerankedResult(
                id: $"chunk-{i + 1}",
                content: $"Content for chunk {i + 1} with enough text to be meaningful.",
                rerankScore: score,
                originalRank: i + 1,
                rerankRank: i + 1));
        }
        return results;
    }

    public static CragEvaluation CreateAcceptEvaluation(double score = 0.85) =>
        new()
        {
            Action = CorrectionAction.Accept,
            RelevanceScore = score,
            Reasoning = "Results are highly relevant"
        };

    public static CragEvaluation CreateRefineEvaluation(double score = 0.5) =>
        new()
        {
            Action = CorrectionAction.Refine,
            RelevanceScore = score,
            Reasoning = "Results need refinement"
        };

    public static CragEvaluation CreateRejectEvaluation(double score = 0.2) =>
        new()
        {
            Action = CorrectionAction.Reject,
            RelevanceScore = score,
            Reasoning = "Results are not relevant"
        };

    public static ComplexityClassification CreateTrivialClassification(double confidence = 0.9) =>
        new()
        {
            Complexity = QueryComplexity.Trivial,
            Confidence = confidence,
            Reasoning = "Query can be answered from general knowledge without retrieval."
        };

    public static ComplexityClassification CreateSimpleClassification(double confidence = 0.85) =>
        new()
        {
            Complexity = QueryComplexity.Simple,
            Confidence = confidence,
            Reasoning = "Direct factual lookup requiring single-pass retrieval."
        };

    public static ComplexityClassification CreateModerateClassification(double confidence = 0.8) =>
        new()
        {
            Complexity = QueryComplexity.Moderate,
            Confidence = confidence,
            Reasoning = "Query requires hybrid retrieval with quality evaluation."
        };

    public static ComplexityClassification CreateComplexClassification(double confidence = 0.75) =>
        new()
        {
            Complexity = QueryComplexity.Complex,
            Confidence = confidence,
            Reasoning = "Multi-hop query requiring iterative retrieval across documents."
        };

    public static ComplexityRoutingConfig CreateComplexityRoutingConfig(
        Action<ComplexityRoutingConfig>? configure = null)
    {
        var config = new ComplexityRoutingConfig
        {
            Enabled = true,
            ConfidenceThreshold = 0.7,
            SimpleTopK = 5,
            ModerateTopK = null,
            ComplexTopK = 15,
            SkipRerankForSimple = true,
            SkipCragForSimple = true,
        };
        configure?.Invoke(config);
        return config;
    }

    public static SubQuery CreateSubQuery(
        string text = "What is the default chunking strategy?",
        int order = 1,
        IReadOnlyList<int>? dependsOnOrders = null) =>
        new()
        {
            Text = text,
            Order = order,
            DependsOnOrders = dependsOnOrders ?? []
        };

    public static DecomposedQuery CreateDecomposedQuery(
        string originalQuery = "Complex multi-part query",
        params string[] subQueryTexts)
    {
        var texts = subQueryTexts.Length > 0
            ? subQueryTexts
            : new[] { "Sub-query 1: first part", "Sub-query 2: second part" };

        var subQueries = texts.Select((text, i) => new SubQuery
        {
            Text = text,
            Order = i + 1,
            DependsOnOrders = i > 0 ? [i] : []
        }).ToList();

        return new DecomposedQuery
        {
            OriginalQuery = originalQuery,
            SubQueries = subQueries
        };
    }

    public static HopResult CreateHopResult(
        SubQuery? subQuery = null,
        IReadOnlyList<RetrievalResult>? results = null,
        double sufficiencyScore = 0.8,
        int hopNumber = 1,
        bool? isSufficient = null) =>
        new()
        {
            SubQuery = subQuery ?? CreateSubQuery(),
            Results = results ?? CreateRetrievalResults(3),
            SufficiencyScore = sufficiencyScore,
            HopNumber = hopNumber,
            IsSufficient = isSufficient ?? sufficiencyScore >= 0.7
        };

    public static IterativeRetrievalResult CreateIterativeRetrievalResult(
        IReadOnlyList<HopResult>? hops = null,
        int totalTokensUsed = 512,
        bool budgetExhausted = false)
    {
        var effectiveHops = hops ?? [CreateHopResult()];
        var aggregated = effectiveHops
            .SelectMany(h => h.Results)
            .GroupBy(r => r.Chunk.Id)
            .Select(g => g.OrderByDescending(r => r.FusedScore).First())
            .ToList();

        return new IterativeRetrievalResult
        {
            Hops = effectiveHops,
            AggregatedResults = aggregated,
            TotalTokensUsed = totalTokensUsed,
            BudgetExhausted = budgetExhausted
        };
    }

    public static FaithfulnessEvaluation CreateFaithfulEvaluation(double score = 0.9) =>
        new()
        {
            IsFaithful = true,
            Score = score,
            SupportedClaims = ["Claim A is supported by chunk-1", "Claim B is supported by chunk-2"],
            HallucinatedClaims = [],
            Reasoning = "All claims are grounded in the retrieved context."
        };

    public static FaithfulnessEvaluation CreateUnfaithfulEvaluation(
        IReadOnlyList<string>? hallucinatedClaims = null) =>
        new()
        {
            IsFaithful = false,
            Score = 0.3,
            SupportedClaims = ["Claim A is supported by chunk-1"],
            HallucinatedClaims = hallucinatedClaims ?? ["Claim X has no source", "Claim Y contradicts chunk-2"],
            Reasoning = "Multiple claims are not grounded in the retrieved context."
        };

    public static MultiHopConfig CreateMultiHopConfig(Action<MultiHopConfig>? configure = null)
    {
        var config = new MultiHopConfig
        {
            Enabled = true,
            MaxHops = 3,
            TokenBudgetPerHop = 1024,
            MinSufficiencyScore = 0.7,
            TopKPerHop = 5,
        };
        configure?.Invoke(config);
        return config;
    }

    public static FaithfulnessConfig CreateFaithfulnessConfig(Action<FaithfulnessConfig>? configure = null)
    {
        var config = new FaithfulnessConfig
        {
            Enabled = true,
            HallucinationThreshold = 0.3,
            RequireCitationSupport = true,
        };
        configure?.Invoke(config);
        return config;
    }

    public static Community CreateCommunity(
        string id = "community_0_1", int level = 0,
        string summary = "A community of related technology entities.",
        IReadOnlyList<string>? nodeIds = null, double modularity = 0.65) =>
        new()
        {
            Id = id, Level = level, Summary = summary,
            NodeIds = nodeIds ?? ["node-1", "node-2", "node-3"],
            Modularity = modularity
        };

    public static MemoryRecord CreateMemoryRecord(
        string id = "mem-1",
        string content = "The user prefers concise answers over verbose explanations.",
        string source = "session-abc", double weight = 0.8,
        DateTimeOffset? createdAt = null, DateTimeOffset? lastAccessedAt = null,
        int accessCount = 1, IReadOnlyDictionary<string, string>? metadata = null) =>
        new()
        {
            Id = id, Content = content, Source = source, Weight = weight,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = lastAccessedAt ?? DateTimeOffset.UtcNow,
            AccessCount = accessCount,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

    public static MemoryQuery CreateMemoryQuery(
        string query = "user preferences", int topK = 10,
        double minWeight = 0.1, string? source = null) =>
        new() { Query = query, TopK = topK, MinWeight = minWeight, Source = source };

    public static GraphNode CreateGraphNode(
        string id = "node-1", string name = "Azure OpenAI",
        string type = "Technology", IReadOnlyList<string>? chunkIds = null,
        IReadOnlyDictionary<string, string>? properties = null) =>
        new()
        {
            Id = id, Name = name, Type = type,
            ChunkIds = chunkIds ?? ["chunk-1"],
            Properties = properties ?? new Dictionary<string, string>()
        };

    public static GraphEdge CreateGraphEdge(
        string id = "edge-1", string sourceNodeId = "node-1",
        string targetNodeId = "node-2", string predicate = "uses",
        string chunkId = "chunk-1",
        IReadOnlyDictionary<string, string>? properties = null) =>
        new()
        {
            Id = id, SourceNodeId = sourceNodeId, TargetNodeId = targetNodeId,
            Predicate = predicate, ChunkId = chunkId,
            Properties = properties ?? new Dictionary<string, string>()
        };

    public static CrossSessionMemoryConfig CreateCrossSessionMemoryConfig(
        Action<CrossSessionMemoryConfig>? configure = null)
    {
        var config = new CrossSessionMemoryConfig
        {
            Enabled = true, DecayRate = 0.05, PruneThreshold = 0.01,
            MaxMemories = 10_000, SyncInterval = TimeSpan.FromMinutes(5),
        };
        configure?.Invoke(config);
        return config;
    }

    public static GraphDatabaseConfig CreateGraphDatabaseConfig(
        Action<GraphDatabaseConfig>? configure = null)
    {
        var config = new GraphDatabaseConfig
        {
            Enabled = true, Provider = "kuzu", DataDirectory = "./data/graph",
        };
        configure?.Invoke(config);
        return config;
    }

    public static RetrievalQualityReport CreateQualityReport(
        double contextPrecision = 0.85,
        double contextRecall = 0.80,
        double faithfulness = 0.90,
        double answerRelevancy = 0.88,
        double overallScore = 0.86) =>
        new()
        {
            ContextPrecision = contextPrecision,
            ContextRecall = contextRecall,
            Faithfulness = faithfulness,
            AnswerRelevancy = answerRelevancy,
            OverallScore = overallScore,
            Reasoning = "Test quality report with high scores across all metrics.",
            EvaluatedAt = DateTimeOffset.UtcNow
        };

    public static RetrievalCostSummary CreateCostSummary(
        int promptTokens = 1500,
        int completionTokens = 500,
        int retrievalCalls = 3,
        double totalLatencyMs = 2500.0) =>
        new()
        {
            TotalTokensUsed = promptTokens + completionTokens,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RetrievalCalls = retrievalCalls,
            TotalLatency = TimeSpan.FromMilliseconds(totalLatencyMs),
            EstimatedCost = (promptTokens * 2.50 / 1_000_000) + (completionTokens * 10.00 / 1_000_000)
        };

    public static SourceRetrievalResult CreateSourceResult(
        string sourceName = "vector",
        int resultCount = 3,
        double latencyMs = 500.0,
        int tokensUsed = 200) =>
        new()
        {
            SourceName = sourceName,
            Results = CreateRetrievalResults(resultCount),
            Latency = TimeSpan.FromMilliseconds(latencyMs),
            TokensUsed = tokensUsed
        };

    public static RetrievalStepConfiguration CreateRetrievalStepConfiguration(
        string query = "What is the architecture of the system?",
        RetrievalStrategy? strategy = null,
        int? topK = null,
        string? collectionName = null,
        bool useMultiSource = false) =>
        new()
        {
            Query = query,
            Strategy = strategy,
            TopK = topK,
            CollectionName = collectionName,
            UseMultiSource = useMultiSource
        };

    public static IOptionsMonitor<AppConfig> CreateConfigMonitor(
        Action<AppConfig>? configure = null)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.Retrieval.TopK = 10;
        appConfig.AI.Rag.Retrieval.RerankTopK = 5;
        appConfig.AI.Rag.Retrieval.RrfK = 60.0;
        appConfig.AI.Rag.Retrieval.EnableHybrid = true;
        appConfig.AI.Rag.Crag.Enabled = true;
        appConfig.AI.Rag.Crag.AcceptThreshold = 0.7;
        appConfig.AI.Rag.Crag.RefineThreshold = 0.4;
        appConfig.AI.Rag.Crag.AllowWebFallback = false;
        appConfig.AI.Rag.QueryTransform.EnableClassification = false;
        appConfig.AI.Rag.QueryTransform.EnableRagFusion = false;
        appConfig.AI.Rag.QueryTransform.EnableHyde = false;
        appConfig.AI.Rag.ComplexityRouting = new ComplexityRoutingConfig
        {
            Enabled = true,
            ConfidenceThreshold = 0.7,
            SimpleTopK = 5,
            ComplexTopK = 15,
            SkipRerankForSimple = true,
            SkipCragForSimple = true,
        };
        appConfig.AI.Rag.MultiHop = new MultiHopConfig
        {
            Enabled = true,
            MaxHops = 3,
            TokenBudgetPerHop = 1024,
            MinSufficiencyScore = 0.7,
            TopKPerHop = 5,
        };
        appConfig.AI.Rag.Faithfulness = new FaithfulnessConfig
        {
            Enabled = true,
            HallucinationThreshold = 0.3,
            RequireCitationSupport = true,
        };
        appConfig.AI.Rag.MultiSource = new MultiSourceConfig
        {
            Enabled = false,
        };

        configure?.Invoke(appConfig);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);
        return monitor.Object;
    }
}
