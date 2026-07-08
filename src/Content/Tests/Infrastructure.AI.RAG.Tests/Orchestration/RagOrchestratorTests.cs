using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class RagOrchestratorTests
{
    private readonly Mock<IHybridRetriever> _mockRetriever = new();
    private readonly Mock<IReranker> _mockReranker = new();
    private readonly Mock<ICragEvaluator> _mockCrag = new();
    private readonly Mock<IRagContextAssembler> _mockAssembler = new();
    private readonly Mock<IGraphRagService> _mockGraphRag = new();
    private readonly Mock<ITaskComplexityClassifier> _mockComplexityClassifier = new();
    private readonly Mock<IRetrievalDecisionGate> _mockDecisionGate = new();
    private readonly Mock<IMultiSourceOrchestrator> _mockMultiSource = new();
    private readonly Mock<IRetrievalCostTracker> _mockCostTracker = new();

    private readonly RagAssembledContext _expectedContext = new()
    {
        AssembledText = "assembled text",
        TotalTokens = 100,
        WasTruncated = false
    };

    private RagOrchestrator CreateOrchestrator(
        Action<AppConfig>? configure = null,
        QueryRouter? router = null,
        IRetrievalSource? webSearchSource = null,
        IFeedbackWeightedScorer? feedbackScorer = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
        var queryRouter = router ?? new QueryRouter(
            Mock.Of<IQueryClassifier>(),
            Mock.Of<IServiceProvider>(),
            config,
            Mock.Of<ILogger<QueryRouter>>());

        return new RagOrchestrator(
            _mockRetriever.Object,
            _mockReranker.Object,
            _mockCrag.Object,
            _mockAssembler.Object,
            _mockGraphRag.Object,
            feedbackScorer: feedbackScorer,
            queryRouter,
            _mockMultiSource.Object,
            _mockComplexityClassifier.Object,
            _mockCostTracker.Object,
            config,
            Mock.Of<ILogger<RagOrchestrator>>(),
            _mockDecisionGate.Object,
            iterativeRetriever: null,
            faithfulnessEvaluator: null,
            webSearchSource: webSearchSource);
    }

    /// <summary>
    /// Builds a real <see cref="QueryRouter"/> that classifies queries as
    /// <paramref name="classifiedType"/> and returns <paramref name="variants"/> from a stub
    /// "rag_fusion" transformer resolved via a real keyed service provider — mirroring the live
    /// classify → transform flow so the orchestrator can fan retrieval out across the variants.
    /// </summary>
    private static QueryRouter CreateVariantRouter(
        IOptionsMonitor<AppConfig> config,
        QueryType classifiedType,
        RetrievalStrategy strategy,
        IReadOnlyList<string> variants)
    {
        var classifier = new Mock<IQueryClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryClassification
            {
                Type = classifiedType,
                Strategy = strategy,
                Confidence = 0.9,
                Reasoning = "test classification"
            });

        var transformer = new Mock<IQueryTransformer>();
        transformer
            .Setup(t => t.TransformAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(variants);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IQueryTransformer>("rag_fusion", transformer.Object);
        var provider = services.BuildServiceProvider();

        return new QueryRouter(classifier.Object, provider, config, Mock.Of<ILogger<QueryRouter>>());
    }

    private void SetupHappyPath(int chunkCount = 3)
    {
        var retrievalResults = RagTestData.CreateRetrievalResults(chunkCount);
        var rerankedResults = RagTestData.CreateRerankedResults(chunkCount);

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);
    }

    [Fact]
    public async Task SearchAsync_AcceptedResults_AssemblesContext()
    {
        SetupHappyPath();
        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("What is clean architecture?");

        result.AssembledText.Should().Be("assembled text");
        result.TotalTokens.Should().Be(100);
        _mockAssembler.Verify(
            a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_RefineAction_RetriesWithModifiedQuery()
    {
        var retrievalResults = RagTestData.CreateRetrievalResults(3);
        var rerankedResults = RagTestData.CreateRerankedResults(3);

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);
        _mockCrag
            .SetupSequence(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRefineEvaluation())
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("ambiguous query");

        result.AssembledText.Should().Be("assembled text");
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _mockCrag.Verify(
            e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SearchAsync_RejectAction_ReturnsEmptyContext()
    {
        var retrievalResults = RagTestData.CreateRetrievalResults(3);
        var rerankedResults = RagTestData.CreateRerankedResults(3);

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRejectEvaluation());

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("irrelevant query");

        result.TotalTokens.Should().Be(0);
        result.AssembledText.Should().Contain("not relevant");
        _mockAssembler.Verify(
            a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_GraphRagStrategy_DelegatesToGraphService()
    {
        var graphContext = new RagAssembledContext
        {
            AssembledText = "graph-based answer",
            TotalTokens = 50,
            WasTruncated = false
        };

        _mockGraphRag
            .Setup(g => g.GlobalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(graphContext);

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync(
            "thematic query",
            strategyOverride: RetrievalStrategy.GraphRag);

        result.AssembledText.Should().Be("graph-based answer");
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_EmptyRetrieval_ReturnsEmptyContext()
    {
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult>());

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("query with no matches");

        result.TotalTokens.Should().Be(0);
        result.AssembledText.Should().Contain("No relevant documents");
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_TrivialQuery_SkipsRetrievalEntirely()
    {
        var trivialClassification = RagTestData.CreateTrivialClassification();
        var skipDecision = new RetrievalDecision
        {
            SkipRetrieval = true,
            TopK = 0,
            UseReranking = false,
            UseCragEvaluation = false,
            Complexity = TaskComplexity.Trivial,
        };

        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(trivialClassification);
        _mockDecisionGate
            .Setup(g => g.Decide(trivialClassification, It.IsAny<int?>()))
            .Returns(skipDecision);

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("What is 2+2?");

        result.AssembledText.Should().BeEmpty();
        result.TotalTokens.Should().Be(0);
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_SimpleQuery_SkipsRerankAndCrag()
    {
        var simpleClassification = RagTestData.CreateSimpleClassification();
        var simpleDecision = new RetrievalDecision
        {
            SkipRetrieval = false,
            TopK = 5,
            UseReranking = false,
            UseCragEvaluation = false,
            Complexity = TaskComplexity.Simple,
        };
        var retrievalResults = RagTestData.CreateRetrievalResults(3);

        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(simpleClassification);
        _mockDecisionGate
            .Setup(g => g.Decide(simpleClassification, It.IsAny<int?>()))
            .Returns(simpleDecision);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.SearchAsync("What is clean architecture?");

        result.AssembledText.Should().Be("assembled text");
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrag.Verify(
            e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_RoutingDisabled_UsesFullPipeline()
    {
        SetupHappyPath();

        // Classifier would return trivial but routing is disabled — should be ignored
        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateTrivialClassification());

        var orchestrator = CreateOrchestrator(cfg =>
            cfg.AI.ModelRouting.Enabled = false);

        var result = await orchestrator.SearchAsync("trivial query with routing off");

        result.AssembledText.Should().Be("assembled text");
        // Full pipeline ran — retriever was called
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_MultiSourceEnabled_UsesMultiSourcePipeline()
    {
        var retrievalResults = RagTestData.CreateRetrievalResults(3);
        var rerankedResults = RagTestData.CreateRerankedResults(3);

        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateModerateClassification());
        _mockMultiSource
            .Setup(m => m.RetrieveFromAllSourcesAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = true;
            cfg.AI.ModelRouting.Enabled = false;
        });

        var result = await orchestrator.SearchAsync("multi-source query");

        result.AssembledText.Should().Be("assembled text");
        _mockMultiSource.Verify(
            m => m.RetrieveFromAllSourcesAsync(It.IsAny<string>(), It.IsAny<int>(), TaskComplexity.Moderate, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_MultiSourceReturnsEmpty_ReturnsEmptyContext()
    {
        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateSimpleClassification());
        _mockMultiSource
            .Setup(m => m.RetrieveFromAllSourcesAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult>());

        var orchestrator = CreateOrchestrator(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = true;
            cfg.AI.ModelRouting.Enabled = false;
        });

        var result = await orchestrator.SearchAsync("empty multi-source query");

        result.TotalTokens.Should().Be(0);
        result.AssembledText.Should().Contain("No relevant documents found across any source");
        _mockAssembler.Verify(
            a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_FusionRetrievalEnabled_FansOutAcrossVariantsAndFuses()
    {
        var variants = new[] { "original query", "variant one", "variant two" };
        var config = RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.ModelRouting.Enabled = false;
            cfg.AI.Rag.QueryTransform.EnableClassification = true;
            cfg.AI.Rag.QueryTransform.EnableRagFusion = true;
            cfg.AI.Rag.QueryTransform.EnableFusionRetrieval = true;
        });
        var router = CreateVariantRouter(
            config, QueryType.MultiHop, RetrievalStrategy.HybridVectorBm25, variants);

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRerankedResults(3));
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator(
            configure: cfg =>
            {
                cfg.AI.ModelRouting.Enabled = false;
                cfg.AI.Rag.QueryTransform.EnableClassification = true;
                cfg.AI.Rag.QueryTransform.EnableRagFusion = true;
                cfg.AI.Rag.QueryTransform.EnableFusionRetrieval = true;
            },
            router: router);

        var result = await orchestrator.SearchAsync("original query");

        result.AssembledText.Should().Be("assembled text");
        // Fan-out: one retrieval per variant (proves the variants are no longer discarded).
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task SearchAsync_FusionRetrievalDisabled_RetrievesOriginalQueryOnly()
    {
        var variants = new[] { "original query", "variant one", "variant two" };
        var config = RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.ModelRouting.Enabled = false;
            cfg.AI.Rag.QueryTransform.EnableClassification = true;
            cfg.AI.Rag.QueryTransform.EnableRagFusion = true;
            cfg.AI.Rag.QueryTransform.EnableFusionRetrieval = false;
        });
        var router = CreateVariantRouter(
            config, QueryType.MultiHop, RetrievalStrategy.HybridVectorBm25, variants);

        SetupHappyPath();

        var orchestrator = CreateOrchestrator(
            configure: cfg =>
            {
                cfg.AI.ModelRouting.Enabled = false;
                cfg.AI.Rag.QueryTransform.EnableClassification = true;
                cfg.AI.Rag.QueryTransform.EnableRagFusion = true;
                cfg.AI.Rag.QueryTransform.EnableFusionRetrieval = false;
            },
            router: router);

        await orchestrator.SearchAsync("original query");

        // Fan-out gated off — legacy single retrieval even though variants were produced.
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_WebFallbackWithWebEnabled_InvokesWebSourceAndAssembles()
    {
        var webSource = new Mock<IRetrievalSource>();
        webSource.SetupGet(s => s.SourceName).Returns("web_search");
        webSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateSourceResult("web_search", resultCount: 2));

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRerankedResults(3));
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateWebFallbackEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator(
            configure: cfg =>
            {
                cfg.AI.ModelRouting.Enabled = false;
                cfg.AI.Rag.Crag.AllowWebFallback = true;
                cfg.AI.Rag.MultiSource.EnabledSources = ["vector", "graph", "web_search"];
            },
            webSearchSource: webSource.Object);

        var result = await orchestrator.SearchAsync("query needing web fallback");

        result.AssembledText.Should().Be("assembled text");
        webSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockAssembler.Verify(
            a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_WebFallback_ReranksMergedSet_SoWebHitsCompeteOnOneScale()
    {
        // Local candidates are reranked onto a calibrated [0,1] scale. Web results arrive carrying a
        // huge raw FusedScore on a foreign scale (999/888). Pre-fix, AppendWebResultsAsync copied that
        // raw score into RerankScore and concatenated it; the assembler orders by RerankScore alone,
        // so the web hits dominated the context purely by scale. The fix re-ranks the merged
        // (local + web) candidate set through the same reranker so every hit is scored on one
        // comparable scale before assembly.
        var webResults = new[]
        {
            RagTestData.CreateRetrievalResult(id: "web-1", content: "Web content 1.", fusedScore: 999.0),
            RagTestData.CreateRetrievalResult(id: "web-2", content: "Web content 2.", fusedScore: 888.0),
        };
        var webSourceResult = new SourceRetrievalResult
        {
            SourceName = "web_search",
            Results = webResults,
            Latency = TimeSpan.FromMilliseconds(250),
            TokensUsed = 120,
        };

        var webSource = new Mock<IRetrievalSource>();
        webSource.SetupGet(s => s.SourceName).Returns("web_search");
        webSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(webSourceResult);

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));

        // Reranker simulates a cross-encoder: it assigns calibrated [0,1] scores to whatever candidate
        // set it is handed, ignoring the incoming (foreign-scale) FusedScore. This is what makes local
        // and web hits comparable — and it means a hit only reaches the assembler with score > 1.0 if
        // its raw FusedScore was copied through WITHOUT going via the reranker (the bug).
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<RetrievalResult> results, int topK, CancellationToken _) =>
                results
                    .Take(topK)
                    .Select((r, i) => new RerankedResult
                    {
                        RetrievalResult = r,
                        RerankScore = Math.Max(0.0, 0.9 - (i * 0.1)),
                        OriginalRank = i + 1,
                        RerankRank = i + 1,
                    })
                    .ToList());

        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateWebFallbackEvaluation());

        IReadOnlyList<RerankedResult>? assembled = null;
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<RerankedResult>, int, CancellationToken>((results, _, _) => assembled = results)
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator(
            configure: cfg =>
            {
                cfg.AI.ModelRouting.Enabled = false;
                cfg.AI.Rag.Crag.AllowWebFallback = true;
                cfg.AI.Rag.MultiSource.EnabledSources = ["vector", "graph", "web_search"];
            },
            webSearchSource: webSource.Object);

        await orchestrator.SearchAsync("query needing web fallback");

        // The reranker must see the merged set — i.e., a candidate list that contains the web chunks.
        _mockReranker.Verify(
            r => r.RerankAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<RetrievalResult>>(list => list.Any(x => x.Chunk.Id.StartsWith("web", StringComparison.Ordinal))),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Nothing handed to the assembler may carry a foreign-scale score: web hits went through the
        // reranker, so every RerankScore sits on the calibrated [0,1] scale (never the raw 999/888).
        assembled.Should().NotBeNull();
        assembled!.Should().Contain(r => r.RetrievalResult.Chunk.Id == "web-1");
        assembled!.Should().OnlyContain(r => r.RerankScore <= 1.0);
    }

    [Fact]
    public async Task SearchAsync_WebFallbackWithFeedbackScorer_ReAppliesFeedbackToMergedSet()
    {
        // Pre-CRAG the local set is reranked then feedback-blended, so a registered scorer's historical
        // weighting is baked into the local ordering. Re-ranking the local+web union resets scores to
        // the reranker's output — discarding that signal — so feedback blending must be re-applied to
        // the merged set. This test proves the local feedback signal survives web-fallback.
        const double feedbackSentinel = 5.0; // Off the reranker's [0,1] scale, so its presence is unambiguous.

        var webResults = new[]
        {
            RagTestData.CreateRetrievalResult(id: "web-1", content: "Web content 1.", fusedScore: 999.0),
        };
        var webSource = new Mock<IRetrievalSource>();
        webSource.SetupGet(s => s.SourceName).Returns("web_search");
        webSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "web_search",
                Results = webResults,
                Latency = TimeSpan.FromMilliseconds(200),
                TokensUsed = 90,
            });

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));

        // Reranker assigns calibrated [0,1] scores; chunk-3 lands last (score 0.7).
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<RetrievalResult> results, int topK, CancellationToken _) =>
                results
                    .Take(topK)
                    .Select((r, i) => new RerankedResult
                    {
                        RetrievalResult = r,
                        RerankScore = Math.Max(0.0, 0.9 - (i * 0.1)),
                        OriginalRank = i + 1,
                        RerankRank = i + 1,
                    })
                    .ToList());

        // Feedback scorer boosts chunk-3 to a sentinel score (historical weighting) and re-sorts.
        // Web results have no feedback history, so they pass through unchanged.
        var feedbackScorer = new Mock<IFeedbackWeightedScorer>();
        feedbackScorer
            .Setup(f => f.BlendFeedbackAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RerankedResult> results, string _, CancellationToken _) =>
                results
                    .Select(r => r.RetrievalResult.Chunk.Id == "chunk-3"
                        ? r with { RerankScore = feedbackSentinel }
                        : r)
                    .OrderByDescending(r => r.RerankScore)
                    .ToList());

        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateWebFallbackEvaluation());

        IReadOnlyList<RerankedResult>? assembled = null;
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<RerankedResult>, int, CancellationToken>((results, _, _) => assembled = results)
            .ReturnsAsync(_expectedContext);

        var orchestrator = CreateOrchestrator(
            configure: cfg =>
            {
                cfg.AI.ModelRouting.Enabled = false;
                cfg.AI.Rag.Crag.AllowWebFallback = true;
                cfg.AI.Rag.MultiSource.EnabledSources = ["vector", "graph", "web_search"];
            },
            webSearchSource: webSource.Object,
            feedbackScorer: feedbackScorer.Object);

        await orchestrator.SearchAsync("query needing web fallback");

        // Feedback blending must run on the merged set (a candidate list that includes the web chunk),
        // not just the pre-CRAG local-only set that the union rerank discarded.
        feedbackScorer.Verify(
            f => f.BlendFeedbackAsync(
                It.Is<IReadOnlyList<RerankedResult>>(list => list.Any(x => x.RetrievalResult.Chunk.Id.StartsWith("web", StringComparison.Ordinal))),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The sentinel boost survived to assembly — local feedback weighting was preserved after merge.
        assembled.Should().NotBeNull();
        assembled!.Should().Contain(r =>
            r.RetrievalResult.Chunk.Id == "chunk-3" && r.RerankScore == feedbackSentinel);
    }

    [Fact]
    public async Task SearchAsync_WebFallbackWithWebDisabled_DoesNotInvokeWebSource()
    {
        var webSource = new Mock<IRetrievalSource>();
        webSource.SetupGet(s => s.SourceName).Returns("web_search");

        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRerankedResults(3));
        _mockCrag
            .Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateWebFallbackEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        // Web source registered but "web_search" NOT in EnabledSources (default) → graceful degrade.
        var orchestrator = CreateOrchestrator(
            configure: cfg => cfg.AI.ModelRouting.Enabled = false,
            webSearchSource: webSource.Object);

        await orchestrator.SearchAsync("query needing web fallback");

        webSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_CostTrackerPresent_RecordsCallAfterResult()
    {
        SetupHappyPath();

        var orchestrator = CreateOrchestrator(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = false;
            cfg.AI.ModelRouting.Enabled = false;
        });

        await orchestrator.SearchAsync("cost-tracked query");

        _mockCostTracker.Verify(
            t => t.RecordCall(It.IsAny<int>(), 0, It.IsAny<TimeSpan>()),
            Times.Once);
    }
}
