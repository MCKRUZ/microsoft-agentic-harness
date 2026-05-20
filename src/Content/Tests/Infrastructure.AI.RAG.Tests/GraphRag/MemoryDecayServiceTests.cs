using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Integration tests for <see cref="MemoryDecayService"/> using a real <see cref="KuzuGraphBackend"/>
/// (SQLite in a temp directory) and a mocked <see cref="ICrossSessionMemoryStore"/>.
/// Each test gets a fresh database; temp directories are cleaned up on dispose.
/// </summary>
public sealed class MemoryDecayServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KuzuGraphBackend _graphBackend;
    private readonly Mock<ICrossSessionMemoryStore> _memoryStoreMock;
    private readonly MemoryDecayService _sut;

    public MemoryDecayServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"decay_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _graphBackend = new KuzuGraphBackend(_tempDir, NullLogger<KuzuGraphBackend>.Instance);
        _memoryStoreMock = new Mock<ICrossSessionMemoryStore>();

        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    CrossSessionMemory = new CrossSessionMemoryConfig
                    {
                        DecayRate = 0.1,
                        PruneThreshold = 0.05
                    }
                }
            }
        };

        var optionsMonitorMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMonitorMock.Setup(m => m.CurrentValue).Returns(config);

        _sut = new MemoryDecayService(
            _graphBackend,
            _memoryStoreMock.Object,
            optionsMonitorMock.Object,
            NullLogger<MemoryDecayService>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _graphBackend.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── ApplyDecayAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDecayAsync_RecentMemory_MinimalDecay()
    {
        // Arrange — memory accessed 1 hour ago; decay=0.1/day → only ~1/24th of a day elapsed
        var node = new GraphNode
        {
            Id = "mem-recent",
            Name = "recent memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.8000",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                ["content"] = "Something recent"
            }
        };
        await _graphBackend.AddNodesAsync([node]);

        // Act
        await _sut.ApplyDecayAsync();

        // Assert — 0.8 * (1-0.1)^(1/24) ≈ 0.7956; must be > 0.78
        var updated = await _graphBackend.GetNodeAsync("mem-recent");
        Assert.NotNull(updated);
        var weight = double.Parse(updated.Properties["weight"]);
        Assert.True(weight > 0.78, $"Expected weight > 0.78 (minimal decay) but got {weight}");
    }

    [Fact]
    public async Task ApplyDecayAsync_OldMemory_SignificantDecay()
    {
        // Arrange — memory accessed 30 days ago; 0.8 * (1-0.1)^30 ≈ 0.034
        var node = new GraphNode
        {
            Id = "mem-old",
            Name = "old memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.8000",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.AddDays(-30).ToString("O"),
                ["content"] = "Something old"
            }
        };
        await _graphBackend.AddNodesAsync([node]);

        // Act
        await _sut.ApplyDecayAsync();

        // Assert — weight must be < 0.1 (significant decay)
        var updated = await _graphBackend.GetNodeAsync("mem-old");
        Assert.NotNull(updated);
        var weight = double.Parse(updated.Properties["weight"]);
        Assert.True(weight < 0.1, $"Expected weight < 0.1 (significant decay) but got {weight}");
    }

    [Fact]
    public async Task ApplyDecayAsync_AccessedMemory_ResetsDecay()
    {
        // Arrange — memory accessed just now; elapsed days ≈ 0 → no decay
        var node = new GraphNode
        {
            Id = "mem-now",
            Name = "just accessed memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.8000",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["content"] = "Something fresh"
            }
        };
        await _graphBackend.AddNodesAsync([node]);

        // Act
        await _sut.ApplyDecayAsync();

        // Assert — weight change < 0.0001 so node properties are unchanged; weight ≈ 0.8
        var retrieved = await _graphBackend.GetNodeAsync("mem-now");
        Assert.NotNull(retrieved);
        var weight = double.Parse(retrieved.Properties["weight"]);
        Assert.True(weight >= 0.799, $"Expected weight ≈ 0.8 but got {weight}");
    }

    // ── PruneAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneAsync_BelowThreshold_RemovesMemory()
    {
        // Arrange — weight=0.01 is below threshold=0.05
        var node = new GraphNode
        {
            Id = "mem-low",
            Name = "low weight memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.0100",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.AddDays(-90).ToString("O"),
                ["content"] = "Nearly forgotten"
            }
        };
        await _graphBackend.AddNodesAsync([node]);

        _memoryStoreMock
            .Setup(m => m.ForgetAsync("mem-low", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.PruneAsync(threshold: 0.05);

        // Assert — node deleted from graph
        var deleted = await _graphBackend.GetNodeAsync("mem-low");
        Assert.Null(deleted);

        // Assert — ForgetAsync called on the memory store
        _memoryStoreMock.Verify(m => m.ForgetAsync("mem-low", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PruneAsync_AboveThreshold_PreservesMemory()
    {
        // Arrange — weight=0.8 is above threshold=0.05
        var node = new GraphNode
        {
            Id = "mem-high",
            Name = "high weight memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.8000",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
                ["content"] = "Well remembered"
            }
        };
        await _graphBackend.AddNodesAsync([node]);

        // Act
        await _sut.PruneAsync(threshold: 0.05);

        // Assert — node still in graph
        var preserved = await _graphBackend.GetNodeAsync("mem-high");
        Assert.NotNull(preserved);

        // Assert — ForgetAsync never called
        _memoryStoreMock.Verify(m => m.ForgetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
