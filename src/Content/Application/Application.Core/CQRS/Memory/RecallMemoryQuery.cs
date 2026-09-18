using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Searches the caller's cross-session memory. Two-source retrieval (session cache, then graph)
/// runs behind <c>IKnowledgeMemory.RecallAsync</c>; results are projected to the wire-safe
/// <see cref="MemoryEntry"/> shape. Quarantined facts are never returned — the memory service
/// enforces that invariant at read time.
/// </summary>
/// <remarks>
/// Like the write side, scoping is server-side: recall only ever sees nodes in the ambient
/// caller's <c>memory:{tenant}:{user}:*</c> namespace (plus tenant-visible corpus entities).
/// </remarks>
public sealed record RecallMemoryQuery : IRequest<Result<IReadOnlyList<MemoryEntry>>>
{
    /// <summary>Natural-language or keyword query to match against remembered facts.</summary>
    public required string Query { get; init; }

    /// <summary>Maximum number of results to return (1–50). Default 5.</summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// Optional exact-match filter on entity type (e.g. <c>"Fact"</c>) — restricts results to that one
    /// memory kind instead of every kind the caller has ever remembered. <see langword="null"/> (the
    /// default) is unfiltered, matching the query's original behavior.
    /// </summary>
    public string? EntityType { get; init; }
}
