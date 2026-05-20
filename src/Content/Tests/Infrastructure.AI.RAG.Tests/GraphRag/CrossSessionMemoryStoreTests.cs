using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Unit tests for <see cref="CrossSessionMemoryStore"/> verifying session-local cache
/// behaviour, weight-based filtering, source filtering, pruning, and graph backend delegation.
/// </summary>
public sealed class CrossSessionMemoryStoreTests : IDisposable
{
    private readonly Mock<IGraphDatabaseBackend> _backendMock;
    private readonly CrossSessionMemoryStore _sut;

    public CrossSessionMemoryStoreTests()
    {
        _backendMock = new Mock<IGraphDatabaseBackend>();
        _backendMock
            .Setup(b => b.GetAllNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var appConfig = new AppConfig();
        appConfig.AI.Rag.CrossSessionMemory.Enabled = true;
        appConfig.AI.Rag.CrossSessionMemory.MaxMemories = 5;
        appConfig.AI.Rag.CrossSessionMemory.PruneThreshold = 0.01;
        appConfig.AI.Rag.CrossSessionMemory.DecayRate = 0.05;
        appConfig.AI.Rag.CrossSessionMemory.SyncInterval = TimeSpan.FromMinutes(30);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        _sut = new CrossSessionMemoryStore(
            _backendMock.Object,
            monitor.Object,
            NullLogger<CrossSessionMemoryStore>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    // ── RememberAsync ────────────────────────────────────────────────────────

    /// <summary>
    /// Storing a memory then recalling with a matching keyword returns the stored record.
    /// </summary>
    [Fact]
    public async Task RememberAsync_NewMemory_StoresInCache()
    {
        // Arrange
        var memory = RagTestData.CreateMemoryRecord(
            id: "mem-store-1",
            content: "The user prefers concise answers",
            source: "session-test");

        // Act
        await _sut.RememberAsync(memory);
        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "concise", topK: 5, minWeight: 0.0));

        // Assert
        Assert.Single(results);
        Assert.Equal("mem-store-1", results[0].Id);
    }

    // ── RecallAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Recalling an existing memory bumps its AccessCount above the initial value.
    /// </summary>
    [Fact]
    public async Task RecallAsync_ExistingMemory_ReturnsAndUpdatesAccessCount()
    {
        // Arrange
        var memory = RagTestData.CreateMemoryRecord(
            id: "mem-access-1",
            content: "cross session knowledge fact",
            source: "session-test",
            accessCount: 0);

        await _sut.RememberAsync(memory);

        // Act
        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "knowledge", topK: 5, minWeight: 0.0));

        // Assert
        Assert.Single(results);
        Assert.True(results[0].AccessCount > 0, "AccessCount should be incremented on recall");
    }

    /// <summary>
    /// Memories with weight below MinWeight are excluded from recall results.
    /// </summary>
    [Fact]
    public async Task RecallAsync_MinWeightFilter_ExcludesBelowThreshold()
    {
        // Arrange — high-weight memory passes the filter, low-weight one does not
        var highWeight = RagTestData.CreateMemoryRecord(
            id: "mem-high", content: "important filtered knowledge",
            source: "session-test", weight: 0.8);

        var lowWeight = RagTestData.CreateMemoryRecord(
            id: "mem-low", content: "weak filtered knowledge",
            source: "session-test", weight: 0.05);

        await _sut.RememberAsync(highWeight);
        await _sut.RememberAsync(lowWeight);

        // Act
        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "filtered knowledge", topK: 10, minWeight: 0.1));

        // Assert
        Assert.Contains(results, r => r.Id == "mem-high");
        Assert.DoesNotContain(results, r => r.Id == "mem-low");
    }

    /// <summary>
    /// Source filter restricts recall to memories whose Source matches exactly.
    /// </summary>
    [Fact]
    public async Task RecallAsync_SourceFilter_OnlyReturnsMatchingSource()
    {
        // Arrange
        var memA = RagTestData.CreateMemoryRecord(
            id: "mem-1", content: "source filter test knowledge",
            source: "session-a", weight: 0.9);

        var memB = RagTestData.CreateMemoryRecord(
            id: "mem-2", content: "source filter test knowledge",
            source: "session-b", weight: 0.9);

        await _sut.RememberAsync(memA);
        await _sut.RememberAsync(memB);

        // Act
        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(
                query: "source filter test",
                topK: 10,
                minWeight: 0.0,
                source: "session-a"));

        // Assert
        Assert.Contains(results, r => r.Id == "mem-1");
        Assert.DoesNotContain(results, r => r.Id == "mem-2");
    }

    // ── ForgetAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Forgetting a memory removes it from the cache and delegates deletion to the graph backend.
    /// </summary>
    [Fact]
    public async Task ForgetAsync_ExistingMemory_RemovesFromCacheAndBackend()
    {
        // Arrange
        var memory = RagTestData.CreateMemoryRecord(
            id: "mem-forget-1", content: "forget this knowledge", source: "session-test");

        await _sut.RememberAsync(memory);

        // Act
        await _sut.ForgetAsync("mem-forget-1");

        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "forget", topK: 5, minWeight: 0.0));

        // Assert — removed from cache
        Assert.DoesNotContain(results, r => r.Id == "mem-forget-1");

        // Assert — backend deletion was called once
        _backendMock.Verify(
            b => b.DeleteNodeAsync("mem-forget-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── ImproveAsync ─────────────────────────────────────────────────────────

    /// <summary>
    /// Applying a positive feedback delta increases the memory weight accordingly.
    /// </summary>
    [Fact]
    public async Task ImproveAsync_ExistingMemory_AdjustsWeight()
    {
        // Arrange
        var memory = RagTestData.CreateMemoryRecord(
            id: "mem-improve-1", content: "improvable knowledge",
            source: "session-test", weight: 0.5);

        await _sut.RememberAsync(memory);

        // Act
        await _sut.ImproveAsync("mem-improve-1", feedbackDelta: 0.2);

        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "improvable", topK: 5, minWeight: 0.0));

        // Assert — weight should be approximately 0.7 (0.5 + 0.2), clamped to [0, 1]
        Assert.Single(results);
        Assert.Equal(0.7, results[0].Weight, precision: 5);
    }

    // ── Pruning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When the store exceeds MaxMemories the lowest-weight entry is pruned on the next Remember.
    /// </summary>
    [Fact]
    public async Task RememberAsync_ExceedsMaxMemories_PrunesLowestWeight()
    {
        // Arrange — add MaxMemories (5) memories with decreasing weights, then one more
        // Weights: 1.0, 0.9, 0.8, 0.7, 0.6 → cache full at 5; adding 6th (weight 0.5) prunes 0.6
        for (var i = 0; i < 5; i++)
        {
            var weight = 1.0 - (i * 0.1);
            await _sut.RememberAsync(RagTestData.CreateMemoryRecord(
                id: $"mem-prune-{i}",
                content: $"prune test memory {i}",
                source: "session-test",
                weight: weight));
        }

        // Act — add 6th memory with the lowest weight; the lowest in the full cache (0.6) gets pruned
        await _sut.RememberAsync(RagTestData.CreateMemoryRecord(
            id: "mem-prune-new",
            content: "prune test new memory",
            source: "session-test",
            weight: 0.95)); // high enough to survive; lowest existing (0.6) gets pruned

        var results = await _sut.RecallAsync(
            RagTestData.CreateMemoryQuery(query: "prune test", topK: 10, minWeight: 0.0));

        // Assert — total should be exactly MaxMemories (5)
        Assert.Equal(5, results.Count);

        // Assert — the lowest-weight entry (mem-prune-4 at weight 0.6) was pruned
        Assert.DoesNotContain(results, r => r.Id == "mem-prune-4");
    }
}
