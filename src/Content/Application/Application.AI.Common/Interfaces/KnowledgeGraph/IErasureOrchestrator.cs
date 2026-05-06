using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Coordinates right-to-erasure across all storage layers: graph nodes/edges,
/// feedback weights, and vector embeddings. Returns an <see cref="ErasureReceipt"/>
/// as proof of compliance.
/// </summary>
public interface IErasureOrchestrator
{
    /// <summary>Erase all data owned by the specified owner (user/tenant).</summary>
    Task<ErasureReceipt> EraseByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Erase specific nodes and all their associated data.</summary>
    Task<ErasureReceipt> EraseByNodeIdsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default);
}
