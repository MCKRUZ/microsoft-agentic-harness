using Application.AI.Common.Interfaces.KnowledgeGraph;

namespace Infrastructure.AI.RAG.Remote;

/// <summary>
/// Local, network-free stand-in for <see cref="IMemoryDecayService"/> when remote memory hosting
/// is enabled. Decay/prune scheduling stays unregistered in this phase of the avatar-hosting
/// migration (the remote memory contract's <c>/prune</c> endpoint is deliberately inert on the
/// remote side too — nothing calls it yet), so both methods are safe no-ops rather than a call to
/// an endpoint that would do nothing useful.
/// </summary>
public sealed class RemoteMemoryDecayService : IMemoryDecayService
{
    /// <inheritdoc />
    public Task ApplyDecayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task PruneAsync(double threshold, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
