using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;
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
/// Verifies that <see cref="MultiSourceOrchestrator"/> forwards the collection name to
/// every fanned-out <see cref="IRetrievalSource"/>. Without this, the Phase-D pipeline
/// would drop a tenant-derived collection on the floor and the vector source would search
/// the shared default collection — a silent cross-tenant leak under ScopedCollections.
/// </summary>
public sealed class MultiSourceOrchestratorCollectionForwardingTests
{
    private readonly Mock<IRetrievalSource> _vectorSource = new();

    private MultiSourceOrchestrator CreateOrchestrator()
    {
        var config = RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.Rag.MultiSource.Enabled = true;
            cfg.AI.Rag.MultiSource.EnabledSources = ["vector"];
            cfg.AI.Rag.MultiSource.SourcesByComplexity = new() { ["Simple"] = ["vector"] };
        });

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRetrievalSource>("vector", _vectorSource.Object);

        return new MultiSourceOrchestrator(
            services.BuildServiceProvider(),
            Mock.Of<IRetrievalCostTracker>(),
            config,
            Mock.Of<ILogger<MultiSourceOrchestrator>>());
    }

    private void SetupVectorSource()
    {
        _vectorSource.Setup(s => s.SourceName).Returns("vector");
        _vectorSource
            .Setup(s => s.RetrieveAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TaskComplexity>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceRetrievalResult
            {
                SourceName = "vector",
                Results = RagTestData.CreateRetrievalResults(1),
                Latency = TimeSpan.FromMilliseconds(1),
                TokensUsed = 0,
            });
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_WithCollectionName_ForwardsItToSources()
    {
        SetupVectorSource();
        var collection = ScopedCollectionName.DeriveForTenant("tenant-a");

        await CreateOrchestrator().RetrieveFromAllSourcesAsync(
            "query", 5, TaskComplexity.Simple, collection);

        _vectorSource.Verify(s => s.RetrieveAsync(
            "query", 5, TaskComplexity.Simple, collection, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveFromAllSourcesAsync_WithoutCollectionName_ForwardsNull()
    {
        SetupVectorSource();

        await CreateOrchestrator().RetrieveFromAllSourcesAsync(
            "query", 5, TaskComplexity.Simple);

        _vectorSource.Verify(s => s.RetrieveAsync(
            "query", 5, TaskComplexity.Simple, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
