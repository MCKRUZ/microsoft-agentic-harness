using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Cross-session memory store supporting Remember/Recall/Forget/Improve operations
/// with session-local cache and background sync to graph backend.
/// </summary>
public interface ICrossSessionMemoryStore
{
    /// <summary>Stores a new memory or updates an existing one. Cached locally, synced on interval.</summary>
    Task RememberAsync(MemoryRecord memory, CancellationToken cancellationToken = default);

    /// <summary>Retrieves memories matching the query, ordered by relevance and weight.</summary>
    Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes a memory by ID from cache and graph backend.</summary>
    Task ForgetAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently purges every cross-session memory owned by <paramref name="ownerId"/> from
    /// both the in-memory cache and the durable graph backend, for right-to-erasure. The owner
    /// is matched after canonicalization (see
    /// <see cref="Domain.AI.KnowledgeGraph.Scoping.ScopeIdentity"/>), so casing differences do
    /// not leave records behind. A whitespace/empty owner purges nothing.
    /// </summary>
    /// <param name="ownerId">The knowledge-scope owner whose memories must be erased.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of distinct memory records actually purged (cache and backend, deduplicated).</returns>
    Task<int> PurgeByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Applies a feedback delta to a memory's weight. Clamped to [0.0, 1.0].</summary>
    Task ImproveAsync(string memoryId, double feedbackDelta, CancellationToken cancellationToken = default);
}
