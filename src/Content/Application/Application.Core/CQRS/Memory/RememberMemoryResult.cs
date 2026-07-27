using Domain.AI.KnowledgeGraph.Models;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// The honest outcome of a <see cref="RememberMemoryCommand"/>: what actually happened to the fact
/// after the memory write gate evaluated it. A rejected or quarantined write still completes the
/// command successfully — the rejection is the expected, reportable result, not an error.
/// </summary>
public sealed record RememberMemoryResult
{
    /// <summary>
    /// Whether the fact was persisted as trusted (<see cref="MemoryWriteOutcome.Persisted"/>),
    /// persisted but excluded from all future recall (<see cref="MemoryWriteOutcome.Quarantined"/>),
    /// or dropped entirely (<see cref="MemoryWriteOutcome.Rejected"/>).
    /// </summary>
    public required MemoryWriteOutcome Outcome { get; init; }

    /// <summary>
    /// The gate's short, log- and audit-safe explanation of the decision
    /// (e.g. <c>"trusted"</c>, <c>"quarantined: DirectOverride"</c>). Never contains the
    /// scanned content itself.
    /// </summary>
    public required string Reason { get; init; }
}
