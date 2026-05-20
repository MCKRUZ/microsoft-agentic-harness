using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// Integration tests verifying multi-source fan-out behavior using keyed DI:
/// parallel source queries, result merging, and deduplication by chunk ID.
/// </summary>
public sealed class MultiSourceIntegrationTests
{
    [Fact]
    public async Task FanOut_QueriesAllEnabledSources_MergesResults()
    {
        var vectorSource = CreateMockSource("vector", [CreateResult("v1", 0.9)]);
        var graphSource = CreateMockSource("graph", [CreateResult("g1", 0.8)]);
        var webSource = CreateMockSource("web_search", [CreateResult("w1", 0.7)]);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", vectorSource.Object);
        services.AddKeyedSingleton<IRetrievalSource>("graph", graphSource.Object);
        services.AddKeyedSingleton<IRetrievalSource>("web_search", webSource.Object);

        var config = CreateConfig(
            enabledSources: ["vector", "graph", "web_search"],
            sourcesByComplexity: new()
            {
                ["Complex"] = ["vector", "graph", "web_search"]
            });

        var sp = services.BuildServiceProvider();
        var sut = new MultiSourceOrchestrator(
            sp, Mock.Of<IRetrievalCostTracker>(), config, NullLogger<MultiSourceOrchestrator>.Instance);

        var results = await sut.RetrieveFromAllSourcesAsync("complex query", 10, QueryComplexity.Complex);

        results.Should().HaveCount(3);
        results.Should().BeInDescendingOrder(r => r.FusedScore);
    }

    [Fact]
    public async Task FanOut_DeduplicatesByChunkId_KeepsHighestScore()
    {
        var source1 = CreateMockSource("vector", [CreateResult("shared-id", 0.9)]);
        var source2 = CreateMockSource("graph", [CreateResult("shared-id", 0.7)]);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", source1.Object);
        services.AddKeyedSingleton<IRetrievalSource>("graph", source2.Object);

        var config = CreateConfig(
            enabledSources: ["vector", "graph"],
            sourcesByComplexity: new() { ["Moderate"] = ["vector", "graph"] });

        var sp = services.BuildServiceProvider();
        var sut = new MultiSourceOrchestrator(
            sp, Mock.Of<IRetrievalCostTracker>(), config, NullLogger<MultiSourceOrchestrator>.Instance);

        var results = await sut.RetrieveFromAllSourcesAsync("test", 10, QueryComplexity.Moderate);

        results.Should().HaveCount(1);
        results[0].FusedScore.Should().Be(0.9, "should keep the higher-scoring duplicate");
    }

    private static Mock<IRetrievalSource> CreateMockSource(string name, IReadOnlyList<RetrievalResult> results)
    {
        var mock = new Mock<IRetrievalSource>();
        mock.Setup(s => s.SourceName).Returns(name);
        mock.Setup(s => s.RetrieveAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = name, Results = results, Latency = TimeSpan.FromMilliseconds(50), TokensUsed = 0
            });
        return mock;
    }

    private static RetrievalResult CreateResult(string chunkId, double score) => new()
    {
        Chunk = new DocumentChunk
        {
            Id = chunkId,
            DocumentId = "doc-1",
            SectionPath = "Test",
            Content = $"Content for {chunkId}",
            Tokens = 5,
            Metadata = new ChunkMetadata
            {
                SourceUri = new Uri("https://test.example.com"),
                CreatedAt = DateTimeOffset.UtcNow
            }
        },
        DenseScore = score,
        SparseScore = 0.0,
        FusedScore = score
    };

    private static IOptionsMonitor<AppConfig> CreateConfig(
        List<string> enabledSources,
        Dictionary<string, List<string>> sourcesByComplexity)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.MultiSource.Enabled = true;
        appConfig.AI.Rag.MultiSource.EnabledSources = enabledSources;
        appConfig.AI.Rag.MultiSource.SourcesByComplexity = sourcesByComplexity;
        appConfig.AI.Rag.MultiSource.SourceTimeout = TimeSpan.FromSeconds(5);
        appConfig.AI.Rag.MultiSource.MaxParallelSources = 5;

        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}
