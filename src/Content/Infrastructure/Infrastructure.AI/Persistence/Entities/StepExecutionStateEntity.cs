using Domain.AI.Planner;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity tracking the runtime execution state of an individual plan step.
/// Has a one-to-one relationship with <see cref="PlanStepEntity"/> via a unique
/// foreign key. Uses an integer version token for optimistic concurrency.
/// </summary>
public sealed class StepExecutionStateEntity
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Unique foreign key to the step this state tracks.</summary>
    public Guid StepId { get; set; }

    /// <summary>Current execution status stored as a string.</summary>
    public StepExecutionStatus Status { get; set; }

    /// <summary>Number of execution attempts made (including the initial attempt).</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the step began executing. Null if not yet started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the step finished executing. Null if still running or not started.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Step output data. Null if the step has not completed or produced no output.</summary>
    public string? Output { get; set; }

    /// <summary>Error message if the step failed. Null on success.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Serialized <see cref="Domain.AI.Attestation.ToolExecutionAttestation"/>. Null for non-tool steps.</summary>
    public string? AttestationJson { get; set; }

    /// <summary>Optimistic concurrency token incremented on each save.</summary>
    public int Version { get; set; }

    // Navigation properties

    /// <summary>The step this execution state belongs to.</summary>
    public PlanStepEntity? Step { get; set; }
}
