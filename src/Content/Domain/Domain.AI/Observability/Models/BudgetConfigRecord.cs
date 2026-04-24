namespace Domain.AI.Observability.Models;

/// <summary>
/// Represents a spending budget alert configuration with hysteresis thresholds.
/// </summary>
public sealed record BudgetConfigRecord
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Period { get; init; }
    public decimal WarnAt { get; init; }
    public decimal CritAt { get; init; }
    public decimal WarnClear { get; init; }
    public decimal CritClear { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset? SilencedUntil { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Represents the current budget state for a given period.
/// </summary>
public sealed record BudgetStateRecord
{
    public Guid ConfigId { get; init; }
    public required string Status { get; init; }
    public decimal CurrentSpend { get; init; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset LastEvaluated { get; init; }
    public DateTimeOffset? LastTransition { get; init; }
}

/// <summary>
/// Represents a budget alert threshold transition event.
/// </summary>
public sealed record BudgetEventRecord
{
    public Guid Id { get; init; }
    public Guid ConfigId { get; init; }
    public string? PrevStatus { get; init; }
    public required string NewStatus { get; init; }
    public decimal Spend { get; init; }
    public decimal Threshold { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
