namespace Domain.AI.Governance;

/// <summary>
/// A single governance decision, serialized as one hash-chained JSONL line in the governance
/// audit trail (<c>governance.jsonl</c>). Promoted from a private nested type inside
/// <c>JsonlGovernanceAuditWriter</c> so it can be returned by a query API (#714) — the same shape
/// that has always been written to disk, unchanged, so existing audit files keep deserializing.
/// </summary>
/// <remarks>
/// <see cref="Action"/> and <see cref="Decision"/> are free-form strings composed differently at
/// each of the 15+ call sites (e.g. <c>"allowed"</c>, <c>"denied"</c>,
/// <c>"classification:{action}"</c>, <c>"blocked:{injectionType}"</c>) — there is no closed
/// vocabulary or enum discriminator for either field.
/// </remarks>
public sealed record GovernanceAuditRecord
{
    /// <summary>When this governance decision was logged.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The agent whose action was evaluated.</summary>
    public required string AgentId { get; init; }

    /// <summary>The action that was evaluated (e.g. a tool name, or an internal operation label).</summary>
    public required string Action { get; init; }

    /// <summary>The governance decision, as free-form text (e.g. "allowed", "denied:{reason}").</summary>
    public required string Decision { get; init; }
}
