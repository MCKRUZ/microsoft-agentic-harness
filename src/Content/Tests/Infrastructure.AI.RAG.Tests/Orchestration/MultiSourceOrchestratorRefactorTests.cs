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

/// <summary>
/// Verifies keyed DI resolution behavior introduced by the MultiSourceOrchestrator refactor.
/// Tests focus on the contract: sources resolved by key, disabled sources filtered, missing
/// registrations handled gracefully.
/// </summary>
public sealed class MultiSourceOrchestratorRefactorTests
{
    private readonly Mock<IRetrievalCostTracker> _mockCostTracker = new();

    private static MultiSourceOrchestrator CreateOrchestrator(
        IServiceProvider serviceProvider,
        Action<AppConfig>? configure = null)
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

        return new MultiSourceOrchestrator(
            serviceProvider,
            new Mock<IRetrievalCostTracker>().Object,
            config,
            Mock.Of<ILogger<MultiSourceOrchestrator>>());
    }

    private static Mock<IRetrievalSource> BuildMockSource(string sourceName, int resultCount = 3, string idPrefix = "")
    {
        var prefix = string.IsNullOrEmpty(idPrefix) ? sourceName : idPrefix;
        var results = Enumerable.Range(1, resultCount)
            .Select(i => RagTestData.CreateRetrievalResult(
                id: $"{prefix}-chunk-{i}",
                content: $"{prefix} content {i}",
                fusedScore: 1.0 - (i * 0.1)))
            .ToList();

        var mock = new Mock<IRetrievalSource>();
        mock.Setup(s => s.SourceName).Returns(sourceName);
        mock.Setup(s => s.RetrieveAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<QueryComplexity>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = sourceName,
                Results = results,
                Latency = TimeSpan.FromMilliseconds(50),
                TokensUsed = 0
            });
        return mock;
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_ResolvesSourcesByKeyFromDI()
    {
        // Arrange — register both sources with their DI keys
        var mockVector = BuildMockSource("vector", resultCount: 2);
        var mockGraph = BuildMockSource("graph", resultCount: 2);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", mockVector.Object);
        services.AddKeyedSingleton<IRetrievalSource>("graph", mockGraph.Object);
        var sp = services.BuildServiceProvider();

        var orchestrator = CreateOrchestrator(sp);

        // Act
        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "architecture query", topK: 10, QueryComplexity.Moderate);

        // Assert — both sources were called via keyed DI resolution
        mockVector.Verify(s => s.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockGraph.Verify(s => s.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_SkipsDisabledSources()
    {
        // Arrange — both sources registered in DI, but config only enables "vector"
        var mockVector = BuildMockSource("vector", resultCount: 3);
        var mockGraph = BuildMockSource("graph", resultCount: 2);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", mockVector.Object);
        services.AddKeyedSingleton<IRetrievalSource>("graph", mockGraph.Object);
        var sp = services.BuildServiceProvider();

        var orchestrator = CreateOrchestrator(sp, cfg =>
        {
            cfg.AI.Rag.MultiSource.EnabledSources = ["vector"];
        });

        // Act
        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Moderate);

        // Assert — graph is in SourcesByComplexity["Moderate"] but not in EnabledSources
        mockVector.Verify(s => s.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockGraph.Verify(s => s.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Never);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_GracefullyHandlesMissingSource()
    {
        // Arrange — "graph" is enabled in config but NOT registered in DI
        var mockVector = BuildMockSource("vector", resultCount: 3);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", mockVector.Object);
        // "graph" deliberately omitted
        var sp = services.BuildServiceProvider();

        var orchestrator = CreateOrchestrator(sp);

        // Act — Moderate complexity requests both vector and graph
        var results = await orchestrator.RetrieveFromAllSourcesAsync(
            "query", topK: 10, QueryComplexity.Moderate);

        // Assert — vector results returned, graph silently skipped (logged warning, no exception)
        mockVector.Verify(s => s.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()),
            Times.Once);
        results.Should().HaveCount(3);
    }
}
