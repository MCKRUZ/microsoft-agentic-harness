namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// Append-only audit log entity recording plan execution events. Uses an
/// auto-increment long primary key for efficient chronological ordering.
/// </summary>
public sealed class PlanExecutionLogEntity
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>Foreign key to the plan this log entry belongs to.</summary>
    public Guid PlanGraphId { get; set; }

    /// <summary>Step that generated this event. Null for plan-level events.</summary>
    public Guid? StepId { get; set; }

    /// <summary>Event type descriptor (e.g. status transition name).</summary>
    public required string EventType { get; set; }

    /// <summary>When this event occurred.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Optional JSON-serialized event details.</summary>
    public string? DetailsJson { get; set; }

    // Navigation properties

    /// <summary>Owning plan graph.</summary>
    public PlanGraphEntity? PlanGraph { get; set; }
}
