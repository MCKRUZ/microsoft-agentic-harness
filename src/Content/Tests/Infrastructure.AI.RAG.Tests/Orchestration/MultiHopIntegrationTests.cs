using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// End-to-end integration tests for the multi-hop retrieval path.
/// </summary>
public sealed class MultiHopIntegrationTests
{
    private readonly Mock<IHybridRetriever> _mockRetriever = new();
    private readonly Mock<IReranker> _mockReranker = new();
    private readonly Mock<ICragEvaluator> _mockCrag = new();
    private readonly Mock<IRagContextAssembler> _mockAssembler = new();
    private readonly Mock<IGraphRagService> _mockGraphRag = new();
    private readonly Mock<IQueryComplexityClassifier> _mockClassifier = new();
    private readonly Mock<IIterativeRetriever> _mockIterativeRetriever = new();
    private readonly Mock<IAnswerFaithfulnessEvaluator> _mockFaithfulness = new();

    private RagOrchestrator CreateOrchestrator(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.ComplexityRouting.Enabled = true;
            c.AI.Rag.MultiHop.Enabled = true;
            c.AI.Rag.Faithfulness.Enabled = true;
            configure?.Invoke(c);
        });

        var gate = new RetrievalDecisionGate(config, Mock.Of<ILogger<RetrievalDecisionGate>>());
        var queryRouter = new QueryRouter(
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
            feedbackScorer: null,
            queryRouter,
            multiSourceOrchestrator: null,
            _mockClassifier.Object,
            costTracker: null,
            config,
            Mock.Of<ILogger<RagOrchestrator>>(),
            gate,
            _mockIterativeRetriever.Object,
            _mockFaithfulness.Object);
    }

    [Fact]
    public async Task ComplexQuery_FullMultiHopPipeline_ReturnsAssembledContext()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification(0.85));

        var iterativeResult = RagTestData.CreateIterativeRetrievalResult(
            hops:
            [
                RagTestData.CreateHopResult(
                    subQuery: RagTestData.CreateSubQuery("What is the architecture?", 1),
                    results: RagTestData.CreateRetrievalResults(3),
                    sufficiencyScore: 0.9,
                    hopNumber: 1,
                    isSufficient: true),
                RagTestData.CreateHopResult(
                    subQuery: RagTestData.CreateSubQuery("What needs to change for multi-tenancy?", 2, [1]),
                    results: RagTestData.CreateRetrievalResults(2),
                    sufficiencyScore: 0.8,
                    hopNumber: 2,
                    isSufficient: true),
            ],
            totalTokensUsed: 800);
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(iterativeResult);

        var rerankedResults = RagTestData.CreateRerankedResults(5);
        _mockReranker
            .Setup(r => r.RerankAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);

        _mockAssembler
            .Setup(a => a.AssembleAsync(
                It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "Comprehensive multi-hop answer covering architecture and multi-tenancy changes.",
                TotalTokens = 350,
                WasTruncated = false,
            });

        _mockFaithfulness
            .Setup(f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateFaithfulEvaluation(0.92));

        var orchestrator = CreateOrchestrator();
        var result = await orchestrator.SearchAsync(
            "Based on the architecture and deployment docs, what changes support multi-tenant GraphRAG?");

        result.AssembledText.Should().Contain("multi-hop answer");
        result.TotalTokens.Should().Be(350);

        _mockClassifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockIterativeRetriever.Verify(r => r.RetrieveIterativelyAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockReranker.Verify(r => r.RerankAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockFaithfulness.Verify(f => f.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ComplexQuery_MultiHopDisabled_UsesStandardPipeline()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var retrievalResults = RagTestData.CreateRetrievalResults(5);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);

        var rerankedResults = RagTestData.CreateRerankedResults(5);
        _mockReranker
            .Setup(r => r.RerankAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);

        _mockCrag
            .Setup(c => c.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());

        _mockAssembler
            .Setup(a => a.AssembleAsync(
                It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "Standard pipeline result",
                TotalTokens = 200,
                WasTruncated = false,
            });

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.MultiHop.Enabled = false);
        var result = await orchestrator.SearchAsync("Complex query with multi-hop disabled");

        result.AssembledText.Should().Be("Standard pipeline result");
        _mockIterativeRetriever.Verify(
            r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ComplexQuery_FaithfulnessDisabled_SkipsFaithfulnessCheck()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var iterativeResult = RagTestData.CreateIterativeRetrievalResult();
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(iterativeResult);

        _mockReranker
            .Setup(r => r.RerankAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRerankedResults(3));

        _mockAssembler
            .Setup(a => a.AssembleAsync(
                It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "Answer without faithfulness check",
                TotalTokens = 100,
                WasTruncated = false,
            });

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.Faithfulness.Enabled = false);
        var result = await orchestrator.SearchAsync("Complex query, no faithfulness");

        result.AssembledText.Should().Be("Answer without faithfulness check");
        _mockFaithfulness.Verify(
            f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
