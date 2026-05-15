namespace Domain.AI.Planner;

/// <summary>
/// A timestamped entry in a plan's execution log, recording step-level events
/// for audit and debugging purposes.
/// </summary>
public sealed record PlanExecutionLogEntry
{
    /// <summary>The plan this entry belongs to.</summary>
    public required PlanId PlanId { get; init; }

    /// <summary>The step that generated this log entry.</summary>
    public required PlanStepId StepId { get; init; }

    /// <summary>When this event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The status transition or event type.</summary>
    public required StepExecutionStatus Status { get; init; }

    /// <summary>Human-readable description of the event.</summary>
    public string? Message { get; init; }

    /// <summary>Execution attempt number (1-based).</summary>
    public int AttemptNumber { get; init; } = 1;
}
