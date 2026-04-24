namespace Domain.AI.Observability.Models;

/// <summary>
/// Represents a content safety evaluation result.
/// </summary>
public sealed record SafetyEventRecord
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public required string Phase { get; init; }
    public required string Outcome { get; init; }
    public string? Category { get; init; }
    public int? Severity { get; init; }
    public string? FilterName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
