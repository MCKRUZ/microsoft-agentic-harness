namespace Domain.AI.Changes;

/// <summary>
/// The agent identity denormalized into a <see cref="ChangeAuditRecord"/> line. Promoted from a
/// private nested type inside <c>JsonlChangeAuditWriter</c> (#714) — same shape, unchanged.
/// </summary>
public sealed record ChangeAuditIdentity
{
    /// <summary>The identity's tenant, if any.</summary>
    public string? Tenant { get; init; }

    /// <summary>The agent's identifier.</summary>
    public required string Agent { get; init; }

    /// <summary>The identity kind's string name (e.g. <c>"ManagedIdentity"</c>).</summary>
    public required string Kind { get; init; }
}
