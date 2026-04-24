using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

public sealed class HybridRetrieverTests
{
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private readonly Mock<IBm25Store> _mockBm25Store = new();
    private readonly Mock<IEmbeddingService> _mockEmbedding = new();
    private readonly ReadOnlyMemory<float> _testEmbedding = new(new float[] { 0.1f, 0.2f, 0.3f });

    public HybridRetrieverTests()
    {
        _mockEmbedding
            .Setup(e => e.EmbedQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
    }

    private HybridRetriever CreateRetriever(bool enableHybrid = true, double rrfK = 60.0)
    {
        var config = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.Retrieval.EnableHybrid = enableHybrid;
            c.AI.Rag.Retrieval.RrfK = rrfK;
        });

        return new HybridRetriever(
            _mockVectorStore.Object,
            _mockBm25Store.Object,
            _mockEmbedding.Object,
            config,
            Mock.Of<ILogger<HybridRetriever>>());
    }

    [Fact]
    public async Task RetrieveAsync_HybridEnabled_CombinesDenseAndSparse()
    {
        var denseResults = new[] { RagTestData.CreateRetrievalResult("d1", denseScore: 0.9) };
        var sparseResults = new[] { RagTestData.CreateRetrievalResult("s1", denseScore: 0.0, sparseScore: 0.8) };

        _mockVectorStore
            .Setup(v => v.SearchAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(denseResults);
        _mockBm25Store
            .Setup(b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sparseResults);

        var retriever = CreateRetriever(enableHybrid: true);

        var results = await retriever.RetrieveAsync("test query", topK: 10);

        results.Should().HaveCount(2);
        results.Select(r => r.Chunk.Id).Should().Contain("d1").And.Contain("s1");
    }

    [Fact]
    public async Task RetrieveAsync_HybridDisabled_UsesDenseOnly()
    {
        var denseResults = RagTestData.CreateRetrievalResults(3);

        _mockVectorStore
            .Setup(v => v.SearchAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(denseResults);

        var retriever = CreateRetriever(enableHybrid: false);

        var results = await retriever.RetrieveAsync("test query", topK: 3);

        results.Should().HaveCount(3);
        _mockBm25Store.Verify(
            b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RetrieveAsync_DenseFails_FallsBackToSparseOnly()
    {
        var sparseResults = new[] { RagTestData.CreateRetrievalResult("s1", sparseScore: 0.7) };

        _mockVectorStore
            .Setup(v => v.SearchAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Vector store unavailable"));
        _mockBm25Store
            .Setup(b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sparseResults);

        var retriever = CreateRetriever(enableHybrid: true);

        var results = await retriever.RetrieveAsync("test query", topK: 10);

        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("s1");
    }

    [Fact]
    public async Task RetrieveAsync_SparseFails_FallsBackToDenseOnly()
    {
        var denseResults = new[] { RagTestData.CreateRetrievalResult("d1", denseScore: 0.95) };

        _mockVectorStore
            .Setup(v => v.SearchAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(denseResults);
        _mockBm25Store
            .Setup(b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BM25 store unavailable"));

        var retriever = CreateRetriever(enableHybrid: true);

        var results = await retriever.RetrieveAsync("test query", topK: 10);

        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("d1");
    }

    [Fact]
    public async Task RetrieveAsync_OverlappingChunks_GetHigherFusedScores()
    {
        var sharedChunk = RagTestData.CreateChunk("shared", "Appears in both searches.");
        var denseResults = new RetrievalResult[]
        {
            new() { Chunk = sharedChunk, DenseScore = 0.9, SparseScore = 0.0, FusedScore = 0.9 },
            RagTestData.CreateRetrievalResult("d-only", denseScore: 0.8)
        };
        var sparseResults = new RetrievalResult[]
        {
            new() { Chunk = sharedChunk, DenseScore = 0.0, SparseScore = 0.7, FusedScore = 0.7 },
            RagTestData.CreateRetrievalResult("s-only", sparseScore: 0.6)
        };

        _mockVectorStore
            .Setup(v => v.SearchAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(denseResults);
        _mockBm25Store
            .Setup(b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sparseResults);

        var retriever = CreateRetriever(enableHybrid: true);

        var results = await retriever.RetrieveAsync("test query", topK: 10);

        var sharedResult = results.First(r => r.Chunk.Id == "shared");
        var denseOnly = results.First(r => r.Chunk.Id == "d-only");
        var sparseOnly = results.First(r => r.Chunk.Id == "s-only");

        sharedResult.FusedScore.Should().BeGreaterThan(denseOnly.FusedScore);
        sharedResult.FusedScore.Should().BeGreaterThan(sparseOnly.FusedScore);
    }
}
