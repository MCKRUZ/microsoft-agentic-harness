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
/// Integration tests verifying the full complexity routing flow:
/// Classifier → Decision Gate → Orchestrator pipeline path.
/// </summary>
public sealed class ComplexityRoutingIntegrationTests
{
    private readonly Mock<IHybridRetriever> _mockRetriever = new();
    private readonly Mock<IReranker> _mockReranker = new();
    private readonly Mock<ICragEvaluator> _mockCrag = new();
    private readonly Mock<IRagContextAssembler> _mockAssembler = new();
    private readonly Mock<IGraphRagService> _mockGraphRag = new();
    private readonly Mock<IQueryComplexityClassifier> _mockClassifier = new();

    private RagOrchestrator CreateOrchestrator(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
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
            gate);
    }

    [Fact]
    public async Task TrivialQuery_NoRetrieverCalled_ReturnsEmptyContext()
    {
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateTrivialClassification(0.95));

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.ComplexityRouting.Enabled = true);
        var result = await orchestrator.SearchAsync("What is 2+2?");

        result.AssembledText.Should().BeEmpty();
        result.TotalTokens.Should().Be(0);
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrag.Verify(
            c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SimpleQuery_RetrievesButSkipsRerankAndCrag()
    {
        var results = RagTestData.CreateRetrievalResults(3);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext { AssembledText = "simple result", TotalTokens = 50, WasTruncated = false });
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateSimpleClassification(0.9));

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.ComplexityRouting.Enabled = true);
        var result = await orchestrator.SearchAsync("What is the default topK?");

        result.AssembledText.Should().Be("simple result");
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrag.Verify(
            c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LowConfidenceTrivial_UpgradedToModerate_UsesFullPipeline()
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
            .Setup(c => c.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext { AssembledText = "full result", TotalTokens = 100, WasTruncated = false });
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplexityClassification
            {
                Complexity = QueryComplexity.Trivial,
                Confidence = 0.4,
                Reasoning = "Low confidence",
            });

        var orchestrator = CreateOrchestrator(c => c.AI.Rag.ComplexityRouting.Enabled = true);
        var result = await orchestrator.SearchAsync("Ambiguous query");

        result.AssembledText.Should().Be("full result");
        // Routed pipeline retrieves once, then delegates to full vector pipeline for CRAG
        // which retrieves again — so retriever is called at least once.
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockReranker.Verify(
            r => r.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
