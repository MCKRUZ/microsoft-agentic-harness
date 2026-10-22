using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Memory retention (#901): a memory not recalled or written since the cutoff goes from the cache and the durable
/// backend; one in use stays, including one whose recent recall the backend has not been told about yet, and nothing but
/// memory nodes is ever touched.
/// </summary>
public sealed class CrossSessionMemoryStoreRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Mock<IGraphDatabaseBackend> _backend = new();
    private readonly List<string> _deleted = [];
    private readonly CrossSessionMemoryStore _store;

    public CrossSessionMemoryStoreRetentionTests()
    {
        _backend.Setup(b => b.DeleteNodesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, CancellationToken>((ids, _) => _deleted.AddRange(ids))
            .ReturnsAsync(NodeDeletionResult.Empty);
        _backend.Setup(b => b.GetAllNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var config = new AppConfig();
        config.AI.Rag.CrossSessionMemory.SyncInterval = TimeSpan.FromHours(1);
        _store = new CrossSessionMemoryStore(
            _backend.Object, Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            NullLogger<CrossSessionMemoryStore>.Instance);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task ABackendMemoryUnusedSinceTheCutoff_IsDeleted_AndARecentOneKept()
    {
        Backend(Memory("old", Cutoff.AddDays(-1)), Memory("recent", Cutoff.AddDays(1)));

        var deleted = await _store.DeleteNotAccessedSinceAsync(Cutoff, CancellationToken.None);

        deleted.Should().Be(1);
        _deleted.Should().Equal("old");
    }

    [Fact]
    public async Task OnlyMemoryNodesAreTouched_NeverDocumentDerivedGraphContent()
    {
        Backend(new GraphNode
        {
            Id = "entity", Name = "entity", Type = "Organization", CreatedAt = Cutoff.AddDays(-5000),
            Properties = new Dictionary<string, string> { ["last_accessed_at"] = Cutoff.AddDays(-5000).ToString("O") },
        });

        (await _store.DeleteNotAccessedSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task AMemoryRecalledSinceTheCutoff_IsKept_EvenBeforeTheRecallIsSynced()
    {
        // The backend still holds the old access time; the cache knows about the recall.
        Backend(Memory("mem", Cutoff.AddDays(-30)));
        await _store.RememberAsync(Record("mem", "the person prefers tea", Cutoff.AddDays(-30)));
        await _store.RecallAsync(new MemoryQuery { Query = "tea" });

        (await _store.DeleteNotAccessedSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOldMemoryInTheCache_IsRemovedFromTheCache()
    {
        await _store.RememberAsync(Record("cached", "the person lives in Leeds", Cutoff.AddDays(-30)));

        (await _store.DeleteNotAccessedSinceAsync(Cutoff, CancellationToken.None)).Should().Be(1);

        (await _store.RecallAsync(new MemoryQuery { Query = "Leeds" })).Should().BeEmpty();
    }

    [Fact]
    public async Task AMemoryWithNoReadableAccessTime_IsNotTreatedAsOld()
    {
        Backend(new GraphNode { Id = "odd", Name = "odd", Type = "Memory", Properties = new Dictionary<string, string>() });

        (await _store.DeleteNotAccessedSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
    }

    private void Backend(params GraphNode[] nodes) =>
        _backend.Setup(b => b.GetAllNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(nodes);

    private static GraphNode Memory(string id, DateTimeOffset lastAccessed) => new()
    {
        Id = id, Name = id, Type = "Memory", OwnerId = "u1",
        Properties = new Dictionary<string, string>
        {
            ["content"] = "x",
            ["last_accessed_at"] = lastAccessed.ToString("O"),
            ["created_at"] = lastAccessed.AddDays(-10).ToString("O"),
        },
    };

    private static MemoryRecord Record(string id, string content, DateTimeOffset lastAccessed) => new()
    {
        Id = id, Content = content, Source = "test", Weight = 1.0,
        CreatedAt = lastAccessed, LastAccessedAt = lastAccessed, AccessCount = 0, OwnerId = "u1",
    };
}
