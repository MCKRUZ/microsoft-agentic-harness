using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Regression tests for the cross-session memory sync race (solution review finding 42).
/// <para>
/// The previous <c>SyncToBackendAsync</c> snapshotted each record, awaited the backend write,
/// and then unconditionally cleared the dirty flag. A concurrent
/// <see cref="CrossSessionMemoryStore.ImproveAsync"/> / <see cref="CrossSessionMemoryStore.RememberAsync"/>
/// that landed between the cache read and the flag clear had its dirty flag erased even though only
/// the OLD value was synced — the new value was never persisted and was lost on restart. The fix
/// clears the dirty flag BEFORE snapshotting, so a concurrent write re-dirties and is picked up by
/// the next flush; a failed flush re-dirties the records it attempted so they retry.
/// </para>
/// </summary>
public sealed class CrossSessionMemoryStoreSolutionReviewFixTests
{
    private readonly Mock<IGraphDatabaseBackend> _backendMock = new();

    /// <summary>Records the last persisted weight per node id across all AddNodesAsync calls.</summary>
    private readonly ConcurrentDictionary<string, string> _persisted = new();

    private CrossSessionMemoryStore CreateSut()
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.CrossSessionMemory.Enabled = true;
        appConfig.AI.Rag.CrossSessionMemory.MaxMemories = 100;
        // Long interval so the background timer never fires; the test drives sync manually.
        appConfig.AI.Rag.CrossSessionMemory.SyncInterval = TimeSpan.FromHours(1);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        return new CrossSessionMemoryStore(
            _backendMock.Object,
            monitor.Object,
            NullLogger<CrossSessionMemoryStore>.Instance);
    }

    private void RecordPersistedWeights(IReadOnlyList<GraphNode> nodes)
    {
        foreach (var node in nodes)
            _persisted[node.Id] = node.Properties["weight"];
    }

    [Fact]
    public async Task SyncToBackendAsync_ConcurrentUpdateDuringFlush_NewValueIsPersistedOnNextSync()
    {
        // Arrange — the backend write blocks until released, recording whatever value it was handed.
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = true;

        _backendMock
            .Setup(b => b.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<GraphNode> nodes, CancellationToken _) =>
            {
                RecordPersistedWeights(nodes);
                if (blocked)
                {
                    blocked = false;
                    writeStarted.TrySetResult();
                    await release.Task;
                }
            });

        var sut = CreateSut();
        await sut.RememberAsync(RagTestData.CreateMemoryRecord(id: "mem-race", weight: 0.5, source: "session-race"));

        // Start the first flush; it captures the OLD (0.5) value and blocks inside the backend.
        var firstSync = sut.SyncToBackendAsync();
        await writeStarted.Task;

        // Act — mutate the record while the flush is in flight (the race window). weight -> 0.8.
        await sut.ImproveAsync("mem-race", feedbackDelta: 0.3);

        release.TrySetResult();
        await firstSync;

        // The interleaved update must remain dirty; a second sync persists the NEW value.
        await sut.SyncToBackendAsync();

        // Assert
        _persisted.TryGetValue("mem-race", out var weight);
        weight.Should().Be("0.8",
            "the update interleaved with the flush must survive and be persisted on the next sync");
    }

    [Fact]
    public async Task SyncToBackendAsync_BackendThrowsThenSucceeds_RecordIsRetried()
    {
        // Arrange — first AddNodesAsync throws, subsequent calls record normally.
        var firstCall = true;
        _backendMock
            .Setup(b => b.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<GraphNode> nodes, CancellationToken _) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    throw new InvalidOperationException("simulated backend failure");
                }

                RecordPersistedWeights(nodes);
                return Task.CompletedTask;
            });

        var sut = CreateSut();
        await sut.RememberAsync(RagTestData.CreateMemoryRecord(id: "mem-retry", weight: 0.6));

        // Act — first sync fails inside the backend; the record must stay dirty for retry.
        await sut.SyncToBackendAsync();
        await sut.SyncToBackendAsync();

        // Assert — the retry sync persisted the record.
        _persisted.TryGetValue("mem-retry", out var weight);
        weight.Should().Be("0.6",
            "a failed flush must leave the entry dirty so a later sync retries it");
    }
}
