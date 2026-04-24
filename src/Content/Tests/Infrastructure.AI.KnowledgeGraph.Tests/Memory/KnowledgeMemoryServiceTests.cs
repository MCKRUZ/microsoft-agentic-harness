using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

/// <summary>
/// Tests for <see cref="KnowledgeMemoryService"/> — remember/recall/forget/improve
/// operations with two-source retrieval and feedback integration.
/// </summary>
public sealed class KnowledgeMemoryServiceTests
{
    private readonly InMemorySessionCache _cache;
    private readonly InMemoryGraphStore _graphStore;
    private readonly Mock<IFeedbackDetector> _feedbackDetector;
    private readonly Mock<IFeedbackStore> _feedbackStore;
    private readonly Mock<IOptionsMonitor<AppConfig>> _configMonitor;
    private readonly KnowledgeMemoryService _service;

    public KnowledgeMemoryServiceTests()
    {
        _cache = new InMemorySessionCache();
        _graphStore = new InMemoryGraphStore(NullLogger<InMemoryGraphStore>.Instance);
        _feedbackDetector = new Mock<IFeedbackDetector>();
        _feedbackStore = new Mock<IFeedbackStore>();
        _configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        _configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { FeedbackAlpha = 0.3 }
                }
            }
        });

        _service = new KnowledgeMemoryService(
            _cache,
            _graphStore,
            _feedbackDetector.Object,
            _feedbackStore.Object,
            _configMonitor.Object,
            NullLogger<KnowledgeMemoryService>.Instance);
    }

    [Fact]
    public async Task Remember_AddsToSessionCache()
    {
        await _service.RememberAsync("Azure", "Cloud platform by Microsoft");

        _cache.Count.Should().Be(1);
        var results = _cache.Search("Azure");
        results.Should().HaveCount(1);
        results[0].Properties["content"].Should().Be("Cloud platform by Microsoft");
    }

    [Fact]
    public async Task Remember_UsesCustomEntityType()
    {
        await _service.RememberAsync("Python", "Programming language", "Technology");

        var results = _cache.Search("Python");
        results[0].Type.Should().Be("Technology");
    }

    [Fact]
    public async Task Recall_FromCacheFirst()
    {
        await _service.RememberAsync("Azure", "Cloud platform");

        var results = await _service.RecallAsync("Azure");

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Azure");
    }

    [Fact]
    public async Task Recall_FallsBackToGraph_WhenCacheEmpty()
    {
        // Seed graph with memory-prefixed ID (matching RememberAsync ID pattern)
        var node = new GraphNode
        {
            Id = "memory:kubernetes", Name = "Kubernetes", Type = "Technology",
            ChunkIds = ["c1"]
        };
        await _graphStore.AddNodesAsync([node]);

        var results = await _service.RecallAsync("Kubernetes");

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Kubernetes");
    }

    [Fact]
    public async Task Recall_DeduplicatesBetweenCacheAndGraph()
    {
        // Add to both cache and graph with same ID
        await _service.RememberAsync("Azure", "From cache");
        var node = new GraphNode
        {
            Id = "memory:azure", Name = "Azure", Type = "Fact",
            Properties = new Dictionary<string, string> { ["content"] = "From graph" },
            ChunkIds = ["c1"]
        };
        await _graphStore.AddNodesAsync([node]);

        var results = await _service.RecallAsync("Azure", maxResults: 10);

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task Forget_RemovesFromCacheAndGraph()
    {
        await _service.RememberAsync("Temp", "Temporary fact");
        await _cache.FlushToGraphAsync(_graphStore);

        await _service.ForgetAsync("Temp");

        _cache.Search("Temp").Should().BeEmpty();
        (await _graphStore.GetNodeAsync("memory:temp")).Should().BeNull();
    }

    [Fact]
    public async Task Improve_WithFeedback_AppliesWeights()
    {
        _feedbackDetector
            .Setup(d => d.DetectFeedbackAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new FeedbackDetectionResult
            {
                FeedbackDetected = true,
                FeedbackScore = 5,
                FeedbackText = "Positive",
                ContainsFollowupQuestion = false
            });

        await _service.ImproveAsync("Great answer!", "Here is info...", ["n1", "n2"]);

        _feedbackStore.Verify(
            f => f.ApplyNodeFeedbackAsync("n1", 5, 0.3, default), Times.Once);
        _feedbackStore.Verify(
            f => f.ApplyNodeFeedbackAsync("n2", 5, 0.3, default), Times.Once);
    }

    [Fact]
    public async Task Improve_NoFeedbackDetected_SkipsWeightUpdate()
    {
        _feedbackDetector
            .Setup(d => d.DetectFeedbackAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new FeedbackDetectionResult
            {
                FeedbackDetected = false,
                ContainsFollowupQuestion = false
            });

        await _service.ImproveAsync("ok", "response", ["n1"]);

        _feedbackStore.Verify(
            f => f.ApplyNodeFeedbackAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), default),
            Times.Never);
    }

    [Fact]
    public async Task Improve_NullDetectorAndStore_SkipsGracefully()
    {
        var service = new KnowledgeMemoryService(
            _cache, _graphStore, null, null,
            _configMonitor.Object,
            NullLogger<KnowledgeMemoryService>.Instance);

        await service.ImproveAsync("test", "response", ["n1"]);
        // Should not throw
    }
}
