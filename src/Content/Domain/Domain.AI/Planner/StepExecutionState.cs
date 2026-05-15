using Domain.AI.Attestation;

namespace Domain.AI.Planner;

/// <summary>
/// Tracks the runtime execution state of an individual plan step, including
/// attempt counts, timing, output, and optional attestation for tool executions.
/// </summary>
public sealed record StepExecutionState
{
    /// <summary>Identifier of the step this state tracks.</summary>
    public required PlanStepId StepId { get; init; }

    /// <summary>Current execution status of the step.</summary>
    public required StepExecutionStatus Status { get; init; }

    /// <summary>Number of execution attempts made (including the initial attempt).</summary>
    public int AttemptCount { get; init; }

    /// <summary>When the step began executing. Null if not yet started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the step finished executing. Null if still running or not started.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Step output data. Null if the step has not completed or produced no output.</summary>
    public string? Output { get; init; }

    /// <summary>Error message if the step failed. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// HMAC-signed attestation of tool execution. Null for non-tool steps
    /// and for tool steps that crashed before attestation could be created.
    /// </summary>
    public ToolExecutionAttestation? Attestation { get; init; }
}
