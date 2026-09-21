using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// Local, network-free stand-in for <see cref="IMemoryAbstractor"/> when remote memory hosting is
/// enabled. The remote memory contract (extract/remember/recall/prune/cross-session) has no
/// abstraction-generation endpoint, and this seam is only ever reached when
/// <c>AppConfig:AI:HarmonicMemory:Mode</c> is not <c>Off</c> — which stays <c>Off</c> for this
/// phase of the avatar-hosting migration. Rather than fabricate a call to an endpoint the remote
/// side does not have, this implementation returns the candidate content itself as the
/// abstraction, with no cue anchors, so a future caller that flips both flags on gets a safe,
/// honest (if low-quality) result instead of an unexpected exception.
/// </summary>
public sealed class RemoteMemoryAbstractor : IMemoryAbstractor
{
    /// <inheritdoc />
    public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryAbstraction { Abstraction = content });
}
