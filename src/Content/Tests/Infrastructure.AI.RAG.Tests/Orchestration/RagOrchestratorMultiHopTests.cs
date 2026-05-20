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
/// Tests for the multi-hop retrieval path in RagOrchestrator.
/// Verifies that Complex-tier queries route through the iterative retriever
/// and that faithfulness evaluation triggers CRAG refinement when needed.
/// </summary>
public sealed class RagOrchestratorMultiHopTests
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
    public async Task SearchAsync_ComplexQuery_UsesIterativeRetriever()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var iterativeResult = RagTestData.CreateIterativeRetrievalResult();
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(iterativeResult);

        var rerankedResults = RagTestData.CreateRerankedResults(3);
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
                AssembledText = "Multi-hop assembled answer",
                TotalTokens = 200,
                WasTruncated = false,
            });

        _mockFaithfulness
            .Setup(f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateFaithfulEvaluation());

        var orchestrator = CreateOrchestrator();
        var result = await orchestrator.SearchAsync(
            "Based on the architecture and deployment docs, what changes support multi-tenant GraphRAG?");

        result.AssembledText.Should().Be("Multi-hop assembled answer");
        _mockIterativeRetriever.Verify(
            r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Complex queries should use IterativeRetriever, not HybridRetriever directly");
    }

    [Fact]
    public async Task SearchAsync_UnfaithfulAnswer_TriggersRefinement()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var iterativeResult = RagTestData.CreateIterativeRetrievalResult();
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(iterativeResult);

        var rerankedResults = RagTestData.CreateRerankedResults(3);
        _mockReranker
            .Setup(r => r.RerankAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);

        var assembleCallCount = 0;
        _mockAssembler
            .Setup(a => a.AssembleAsync(
                It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                assembleCallCount++;
                return new RagAssembledContext
                {
                    AssembledText = assembleCallCount == 1 ? "Unfaithful first attempt" : "Refined faithful answer",
                    TotalTokens = 150,
                    WasTruncated = false,
                };
            });

        _mockFaithfulness
            .Setup(f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateUnfaithfulEvaluation());

        _mockCrag
            .Setup(c => c.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());

        var orchestrator = CreateOrchestrator();
        var result = await orchestrator.SearchAsync("Complex unfaithful query");

        _mockFaithfulness.Verify(
            f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCrag.Verify(
            c => c.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_FaithfulAnswer_ReturnsDirectly()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var iterativeResult = RagTestData.CreateIterativeRetrievalResult();
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(iterativeResult);

        var rerankedResults = RagTestData.CreateRerankedResults(3);
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
                AssembledText = "Faithful multi-hop answer",
                TotalTokens = 180,
                WasTruncated = false,
            });

        _mockFaithfulness
            .Setup(f => f.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateFaithfulEvaluation());

        var orchestrator = CreateOrchestrator();
        var result = await orchestrator.SearchAsync("Complex faithful query");

        result.AssembledText.Should().Be("Faithful multi-hop answer");
        _mockCrag.Verify(
            c => c.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Faithful answer should not trigger CRAG refinement");
    }

    [Fact]
    public async Task SearchAsync_MultiHopDisabled_FallsBackToStandardRouting()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var retrievalResults = RagTestData.CreateRetrievalResults(3);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);

        var rerankedResults = RagTestData.CreateRerankedResults(3);
        _mockReranker
            .Setup(r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rerankedResults);

        _mockCrag
            .Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());

        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext { AssembledText = "Standard pipeline result", TotalTokens = 100, WasTruncated = false });

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.MultiHop.Enabled = false);
        var result = await orchestrator.SearchAsync("Complex query but multi-hop disabled");

        _mockIterativeRetriever.Verify(
            r => r.RetrieveIterativelyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Should not use iterative retriever when multi-hop is disabled");
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "Should fall back to standard hybrid retriever");
    }

    [Fact]
    public async Task SearchAsync_MultiHopReturnsNoResults_ReturnsEmptyContext()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateComplexClassification());

        var emptyResult = new IterativeRetrievalResult
        {
            Hops = [RagTestData.CreateHopResult(results: [])],
            AggregatedResults = [],
            TotalTokensUsed = 64,
            BudgetExhausted = false,
        };
        _mockIterativeRetriever
            .Setup(r => r.RetrieveIterativelyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);

        var orchestrator = CreateOrchestrator();
        var result = await orchestrator.SearchAsync("Complex query with no matching docs");

        result.TotalTokens.Should().Be(0);
        result.AssembledText.Should().Contain("No relevant documents found");
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Should not rerank when iterative retrieval returned 0 results");
    }
}
