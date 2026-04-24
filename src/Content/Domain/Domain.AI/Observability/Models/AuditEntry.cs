namespace Domain.AI.Observability.Models;

/// <summary>
/// Represents an audited operation in the observability system.
/// </summary>
public sealed record AuditEntry
{
    public Guid Id { get; init; }
    public required string Operation { get; init; }
    public required string Source { get; init; }
    public Guid? SessionId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
