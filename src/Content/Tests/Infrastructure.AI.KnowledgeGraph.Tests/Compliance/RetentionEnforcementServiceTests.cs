using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

public sealed class RetentionEnforcementServiceTests
{
    [Fact]
    public async Task EnforceRetention_DeletesExpiredNodes()
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        var erasureOrchestrator = new Mock<IErasureOrchestrator>();
        erasureOrchestrator
            .Setup(e => e.EraseByNodeIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErasureReceipt
            {
                RequestId = "test", ScopeId = "system",
                RequestedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                NodesDeleted = 1, EdgesDeleted = 0, FeedbackWeightsDeleted = 0, VectorEmbeddingsDeleted = 0
            });

        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // Add an expired node and a valid node
        await store.AddNodesAsync([
            new GraphNode
            {
                Id = "expired", Name = "Old", Type = "Fact",
                CreatedAt = now.AddDays(-400), ExpiresAt = now.AddDays(-35), OwnerId = "u1"
            },
            new GraphNode
            {
                Id = "valid", Name = "New", Type = "Fact",
                CreatedAt = now, ExpiresAt = now.AddDays(365), OwnerId = "u1"
            }
        ]);

        var service = new RetentionEnforcementService(
            store,
            ScopeFactoryFor(erasureOrchestrator.Object),
            Mock.Of<ILogger<RetentionEnforcementService>>());

        await service.EnforceRetentionAsync(now, CancellationToken.None);

        erasureOrchestrator.Verify(e => e.EraseByNodeIdsAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Contains("expired") && !ids.Contains("valid")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceRetention_NoExpiredNodes_DoesNothing()
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        var erasureOrchestrator = new Mock<IErasureOrchestrator>();

        await store.AddNodesAsync([
            new GraphNode
            {
                Id = "valid", Name = "New", Type = "Fact",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(365)
            }
        ]);

        var service = new RetentionEnforcementService(
            store,
            ScopeFactoryFor(erasureOrchestrator.Object),
            Mock.Of<ILogger<RetentionEnforcementService>>());

        await service.EnforceRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        erasureOrchestrator.Verify(
            e => e.EraseByNodeIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Builds a real <see cref="IServiceScopeFactory"/> that resolves the given orchestrator from a
    /// fresh scope, mirroring how the singleton hosted service obtains the scoped dependency at runtime.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(IErasureOrchestrator orchestrator)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => orchestrator);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
