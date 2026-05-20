using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class MultiSourceOrchestratorTests
{
    private readonly Mock<IRetrievalSource> _mockVectorSource = new();
    private readonly Mock<IRetrievalSource> _mockGraphSource = new();
    private readonly Mock<IRetrievalCostTracker> _mockCostTracker = new();

    private MultiSourceOrchestrator CreateOrchestrator(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = true;
            cfg.AI.Rag.MultiSource.EnabledSources = ["vector", "graph"];
            cfg.AI.Rag.MultiSource.SourcesByComplexity = new()
            {
                ["Trivial"] = ["vector"],
                ["Simple"] = ["vector"],
                ["Moderate"] = ["vector", "graph"],
                ["Complex"] = ["vector", "graph", "web_search", "sql_database"]
            };
            configure?.Invoke(cfg);
        });

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", _mockVectorSource.Object);
        services.AddKeyedSingleton<IRetrievalSource>("graph", _mockGraphSource.Object);
        var sp = services.BuildServiceProvider();

        return new MultiSourceOrchestrator(sp, _mockCostTracker.Object, config, Mock.Of<ILogger<MultiSourceOrchestrator>>());
    }

    private void SetupVectorResults(int count = 3)
    {
        _mockVectorSource.Setup(s => s.SourceName).Returns("vector");
        _mockVectorSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "vector",
                Results = RagTestData.CreateRetrievalResults(count),
                Latency = TimeSpan.FromMilliseconds(50),
                TokensUsed = 0
            });
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

        _mockGraphSource.Setup(s => s.SourceName).Returns("graph");
        _mockGraphSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "graph",
                Results = results,
                Latency = TimeSpan.FromMilliseconds(80),
                TokensUsed = 0
            });
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
        _mockVectorSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
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
        _mockVectorSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_ComplexQuery_AllSources()
    {
        SetupVectorResults(3);
        SetupGraphResults(2);
        var orchestrator = CreateOrchestrator();

        // Complex maps to vector + graph + web_search + sql_database, but only vector and graph are enabled
        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "complex multi-faceted query", topK: 10, QueryComplexity.Complex);

        results.Should().HaveCountGreaterThanOrEqualTo(3);
        _mockVectorSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGraphSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_SourceTimeout_GracefulDegradation()
    {
        SetupVectorResults(3);
        _mockGraphSource.Setup(s => s.SourceName).Returns("graph");
        _mockGraphSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, QueryComplexity _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new SourceRetrievalResult
                {
                    SourceName = "graph",
                    Results = [],
                    Latency = TimeSpan.Zero,
                    TokensUsed = 0
                };
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

        _mockVectorSource.Setup(s => s.SourceName).Returns("vector");
        _mockVectorSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "vector",
                Results = [sharedChunk],
                Latency = TimeSpan.FromMilliseconds(50),
                TokensUsed = 0
            });

        _mockGraphSource.Setup(s => s.SourceName).Returns("graph");
        _mockGraphSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "graph",
                Results = [higherScoreChunk],
                Latency = TimeSpan.FromMilliseconds(80),
                TokensUsed = 0
            });

        var orchestrator = CreateOrchestrator();

        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Moderate);

        results.Should().HaveCount(1);
        results[0].FusedScore.Should().Be(0.9);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_AllSourcesFail_ReturnsEmpty()
    {
        _mockVectorSource.Setup(s => s.SourceName).Returns("vector");
        _mockVectorSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Vector store unavailable"));

        _mockGraphSource.Setup(s => s.SourceName).Returns("graph");
        _mockGraphSource
            .Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
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
        _mockGraphSource.Verify(
            s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
