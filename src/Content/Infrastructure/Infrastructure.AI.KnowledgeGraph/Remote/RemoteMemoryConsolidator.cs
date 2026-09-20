using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// Local, network-free stand-in for <see cref="IMemoryConsolidator"/> when remote memory hosting
/// is enabled. The remote memory contract has no consolidation endpoint, and this seam is only
/// ever reached in <c>HarmonicMemoryMode.Full</c> — which this phase of the avatar-hosting
/// migration never enables. Always decides <see cref="MemoryConsolidationDecision.Create"/>,
/// matching the interface's own documented safe default when no similar existing entries are
/// available, rather than fabricating a call to an endpoint the remote side does not have.
/// </summary>
public sealed class RemoteMemoryConsolidator : IMemoryConsolidator
{
    /// <inheritdoc />
    public Task<MemoryConsolidationDecision> ConsolidateAsync(
        MemoryAbstraction candidate,
        string candidateValue,
        IReadOnlyList<ExistingMemory> similarExisting,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryConsolidationDecision.Create());
}
