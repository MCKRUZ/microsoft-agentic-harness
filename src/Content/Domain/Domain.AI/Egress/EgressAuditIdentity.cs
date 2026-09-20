namespace Domain.AI.Egress;

/// <summary>
/// The agent identity denormalized into an <see cref="EgressAuditRecord"/> line, for replayability
/// without joining back to an identity store.
/// </summary>
public sealed record EgressAuditIdentity
{
    /// <summary>The tenant the agent belongs to, if any.</summary>
    public string? Tenant { get; init; }

    /// <summary>The agent's identifier.</summary>
    public required string Agent { get; init; }

    /// <summary>The agent identity kind, as text (e.g. <c>"ManagedIdentity"</c>).</summary>
    public required string Kind { get; init; }
}
