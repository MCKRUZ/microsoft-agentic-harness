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

public sealed class RagOrchestratorTests
{
    private readonly Mock<IHybridRetriever> _mockRetriever = new();
    private readonly Mock<IReranker> _mockReranker = new();
    private readonly Mock<ICragEvaluator> _mockCrag = new();
    private readonly Mock<IRagContextAssembler> _mockAssembler = new();
    private readonly Mock<IGraphRagService> _mockGraphRag = new();

    private readonly RagAssembledContext _expectedContext = new()
    {
        AssembledText = "assembled text",
        TotalTokens = 100,
        WasTruncated = false
    };

    private RagOrchestrator CreateOrchestrator(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
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
            config,
            Mock.Of<ILogger<RagOrchestrator>>());
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
}
