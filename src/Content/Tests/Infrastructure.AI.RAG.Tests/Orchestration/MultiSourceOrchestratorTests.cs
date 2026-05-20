using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class MultiSourceOrchestratorTests
{
    private readonly Mock<IHybridRetriever> _mockHybridRetriever = new();
    private readonly Mock<IGraphRagService> _mockGraphRag = new();
    private readonly Mock<IRetrievalCostTracker> _mockCostTracker = new();

    private MultiSourceOrchestrator CreateOrchestrator(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = true;
            cfg.AI.Rag.MultiSource.EnabledSources = ["vector", "graph", "web"];
            configure?.Invoke(cfg);
        });

        return new MultiSourceOrchestrator(
            _mockHybridRetriever.Object,
            _mockGraphRag.Object,
            _mockCostTracker.Object,
            config,
            Mock.Of<ILogger<MultiSourceOrchestrator>>());
    }

    private void SetupVectorResults(int count = 3)
    {
        _mockHybridRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(count));
    }

    private void SetupGraphResults(int count = 2)
    {
        var results = new List<RetrievalResult>();
        for (var i = 0; i < count; i++)
        {
            results.Add(RagTestData.CreateRetrievalResult(
                id: $"graph-chunk-{i + 1}",
                content: $"Graph content {i + 1}",
                denseScore: 0.8 - (i * 0.1),
                sparseScore: 0.0,
                fusedScore: 0.8 - (i * 0.1)));
        }
        _mockGraphRag
            .Setup(g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_SimpleQuery_VectorOnly()
    {
        SetupVectorResults(3);
        SetupGraphResults(2);
        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "simple query", topK: 5, QueryComplexity.Simple);

        results.Should().HaveCount(3);
        _mockHybridRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphRag.Verify(
            g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_ModerateQuery_VectorAndGraph()
    {
        SetupVectorResults(3);
        SetupGraphResults(2);
        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "moderate query", topK: 10, QueryComplexity.Moderate);

        results.Should().HaveCount(5);
        _mockHybridRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphRag.Verify(
            g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_ComplexQuery_AllSources()
    {
        SetupVectorResults(3);
        SetupGraphResults(2);
        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "complex multi-faceted query", topK: 10, QueryComplexity.Complex);

        results.Should().HaveCountGreaterThanOrEqualTo(3);
        _mockHybridRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphRag.Verify(
            g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_SourceTimeout_GracefulDegradation()
    {
        SetupVectorResults(3);
        _mockGraphRag
            .Setup(g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return (IReadOnlyList<RetrievalResult>)[];
            });

        var orchestrator = CreateOrchestrator(cfg =>
        {
            cfg.AI.Rag.MultiSource.SourceTimeout = TimeSpan.FromMilliseconds(100);
        });

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Moderate);

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_DuplicateChunks_Deduplicated()
    {
        var sharedChunk = RagTestData.CreateRetrievalResult(
            id: "shared-chunk", content: "shared", fusedScore: 0.7);
        var higherScoreChunk = RagTestData.CreateRetrievalResult(
            id: "shared-chunk", content: "shared", fusedScore: 0.9);

        _mockHybridRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult> { sharedChunk });
        _mockGraphRag
            .Setup(g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult> { higherScoreChunk });

        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Moderate);

        results.Should().HaveCount(1);
        results[0].FusedScore.Should().Be(0.9);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_AllSourcesFail_ReturnsEmpty()
    {
        _mockHybridRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Vector store unavailable"));
        _mockGraphRag
            .Setup(g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Graph unavailable"));

        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Complex);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_DisabledSource_Skipped()
    {
        SetupVectorResults(3);
        SetupGraphResults(2);
        var orchestrator = CreateOrchestrator(cfg =>
        {
            cfg.AI.Rag.MultiSource.EnabledSources = ["vector"];
        });

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Complex);

        results.Should().HaveCount(3);
        _mockGraphRag.Verify(
            g => g.LocalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
