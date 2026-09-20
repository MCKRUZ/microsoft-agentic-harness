using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.RAG.Remote;

/// <summary>
/// Local, network-free stand-in for <see cref="ICrossSessionMemoryStore"/> when remote memory
/// hosting is enabled. This seam backs the decay/cross-session-sync scheduler, which stays
/// unregistered in this phase of the avatar-hosting migration (the remote memory contract's
/// <c>/cross-session/{action}</c> endpoints are deliberately inert on the remote side too — no
/// scheduler calls them yet). Behaves as an always-empty store: writes and improvements are
/// accepted and discarded, recalls return nothing, and purges remove nothing, rather than routing
/// to an endpoint nothing actually consumes yet.
/// </summary>
public sealed class RemoteCrossSessionMemoryStore : ICrossSessionMemoryStore
{
    /// <inheritdoc />
    public Task RememberAsync(MemoryRecord memory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryRecord>>([]);

    /// <inheritdoc />
    public Task ForgetAsync(string memoryId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> PurgeByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    /// <inheritdoc />
    public Task ImproveAsync(string memoryId, double feedbackDelta, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
