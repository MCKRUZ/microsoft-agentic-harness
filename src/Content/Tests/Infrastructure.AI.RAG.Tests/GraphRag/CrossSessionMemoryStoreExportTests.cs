using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Personal-data export (#902) for <see cref="CrossSessionMemoryStore"/>: the copy holds the person's memories from both
/// places erasure purges them, the in-process cache and the durable backend, and nothing of anyone else's.
/// </summary>
/// <remarks>
/// The cache starts empty at every restart while the backend keeps memories across them, so a copy read from the cache
/// alone would miss everything from earlier runs and still look complete. The backend also holds other subsystems' nodes
/// owned by the same person; only memory nodes are the store's.
/// </remarks>
public sealed class CrossSessionMemoryStoreExportTests : IDisposable
{
    private readonly Mock<IGraphDatabaseBackend> _backend = new();
    private readonly List<GraphNode> _backendNodes = [];
    private readonly CrossSessionMemoryStore _sut;

    public CrossSessionMemoryStoreExportTests()
    {
        _backend.Setup(b => b.GetAllNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _backendNodes);
        _backend.Setup(b => b.GetNodesByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string owner, CancellationToken _) => [.. _backendNodes.Where(n => n.OwnerId == owner)]);

        var appConfig = new AppConfig();
        appConfig.AI.Rag.CrossSessionMemory.Enabled = true;
        appConfig.AI.Rag.CrossSessionMemory.MaxMemories = 50;
        appConfig.AI.Rag.CrossSessionMemory.SyncInterval = TimeSpan.FromMinutes(30);
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        _sut = new CrossSessionMemoryStore(_backend.Object, monitor.Object, NullLogger<CrossSessionMemoryStore>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task ExportSubjectAsync_CacheAndBackend_ReturnsTheSubjectsMemoriesFromBoth()
    {
        await _sut.RememberAsync(Memory("mem-cached", "prefers short answers", "User-1"));
        await _sut.RememberAsync(Memory("mem-theirs", "someone else", "user-2"));
        _backendNodes.Add(MemoryNode("mem-synced", "works in finance", "user-1"));
        _backendNodes.Add(MemoryNode("mem-backend-theirs", "someone else", "user-2"));
        _backendNodes.Add(new GraphNode { Id = "corpus-1", Name = "corpus", Type = "Entity", OwnerId = "user-1" });

        var records = await _sut.ExportSubjectAsync(new ExportSubject(" USER-1 ", []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().Equal(["mem-cached", "mem-synced"],
            "the cache, the backend's memories from earlier runs, owner matched canonically, and no other node type");
        Text(records).Should().Contain("prefers short answers").And.Contain("works in finance");
        Text(records).Should().NotContain("someone else");
        records.Should().AllSatisfy(r => r.TryGetProperty("ownerId", out _).Should().BeFalse());
    }

    [Fact]
    public async Task ExportSubjectAsync_MemoryInBoth_IsReturnedOnceAsTheCacheHoldsIt()
    {
        await _sut.RememberAsync(Memory("mem-1", "the fresher text", "user-1"));
        _backendNodes.Add(MemoryNode("mem-1", "the text the last sync wrote", "user-1"));

        var records = await _sut.ExportSubjectAsync(new ExportSubject("user-1", []), CancellationToken.None);

        records.Should().ContainSingle().Which.GetProperty("content").GetString().Should().Be("the fresher text");
    }

    [Fact]
    public async Task ExportSubjectAsync_HoldsExactlyWhatPurgeThenDeletes()
    {
        await _sut.RememberAsync(Memory("mem-a", "alpha", "user-1"));
        await _sut.RememberAsync(Memory("mem-b", "beta", "user-1"));
        _backendNodes.Add(MemoryNode("mem-b", "beta", "user-1"));
        _backendNodes.Add(MemoryNode("mem-c", "gamma", "user-1"));
        _backendNodes.Add(new GraphNode { Id = "corpus-1", Name = "corpus", Type = "Entity", OwnerId = "user-1" });

        var exported = await _sut.ExportSubjectAsync(new ExportSubject("user-1", []), CancellationToken.None);
        var purged = await _sut.PurgeByOwnerAsync("user-1");

        exported.Should().HaveCount(purged).And.HaveCount(3);
    }

    [Fact]
    public async Task ExportSubjectAsync_BackendNode_CarriesWhatTheSyncWrote()
    {
        _backendNodes.Add(MemoryNode("mem-1", "works in finance", "user-1"));

        var record = (await _sut.ExportSubjectAsync(new ExportSubject("user-1", []), CancellationToken.None)).Single();

        record.GetProperty("source").GetString().Should().Be("session-test");
        record.GetProperty("weight").GetDouble().Should().Be(0.75);
        record.GetProperty("accessCount").GetInt32().Should().Be(3);
        record.GetProperty("createdAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    }

    private static MemoryRecord Memory(string id, string content, string ownerId) => new()
    {
        Id = id,
        Content = content,
        Source = "session-test",
        Weight = 0.9,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        OwnerId = ownerId,
    };

    /// <summary>A memory node the way the store's sync writes one to the backend.</summary>
    private static GraphNode MemoryNode(string id, string content, string ownerId) => new()
    {
        Id = id,
        Name = id,
        Type = "Memory",
        OwnerId = ownerId,
        Properties = new Dictionary<string, string>
        {
            ["content"] = content,
            ["source"] = "session-test",
            ["weight"] = "0.75",
            ["access_count"] = "3",
            ["last_accessed_at"] = "2026-10-02T09:00:00.0000000+00:00",
            ["created_at"] = "2026-10-01T09:00:00.0000000+00:00",
        },
    };

    private static string Text(IEnumerable<JsonElement> records) => string.Join("\n", records.Select(r => r.GetRawText()));
}
