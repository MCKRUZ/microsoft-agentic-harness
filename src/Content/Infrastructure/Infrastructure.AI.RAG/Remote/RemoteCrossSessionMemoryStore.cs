using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.RAG.Remote;

/// <summary>
/// Local, network-free stand-in for <see cref="ICrossSessionMemoryStore"/> when remote memory
/// hosting is enabled. This seam backs the decay/cross-session-sync scheduler, which stays
/// unregistered in this phase of the avatar-hosting migration (the remote memory contract's
/// <c>/cross-session/{action}</c> endpoints are deliberately inert on the remote side too — no
/// scheduler calls them yet). Behaves as an always-empty store: writes and improvements are
/// accepted and discarded, recalls return nothing, rather than routing to an endpoint nothing
/// actually consumes yet.
/// </summary>
/// <remarks>
/// <see cref="PurgeByOwnerAsync"/> throws instead of returning <c>0</c>, unlike the other members
/// here — <c>Infrastructure.AI.KnowledgeGraph.Compliance.DefaultErasureOrchestrator</c> resolves
/// this interface as the sweep step for a right-to-erasure request, and a silent <c>0</c> would let
/// that orchestrator report <c>Completeness = Full</c> while the subject's facts remain in the
/// remote store, which has no delete endpoint at all (the same reasoning as
/// <c>RemoteKnowledgeMemory.ForgetAsync</c>). The orchestrator catches this specific exception and
/// downgrades the receipt to <c>Partial</c> with a truthful reason instead of letting it fail the
/// whole sweep.
/// </remarks>
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
    /// <exception cref="NotImplementedException">
    /// Always thrown — see the class remarks for why a silent <c>0</c> would be a right-to-erasure
    /// over-report.
    /// </exception>
    public Task<int> PurgeByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "RemoteCrossSessionMemoryStore.PurgeByOwnerAsync has no remote endpoint to call — " +
            "reporting 0 purged here would let the erasure orchestrator certify a subject's " +
            "remote-stored facts as erased when they are not.");

    /// <inheritdoc />
    public Task ImproveAsync(string memoryId, double feedbackDelta, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
