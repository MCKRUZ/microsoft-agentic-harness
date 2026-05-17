using Domain.AI.Attestation;

namespace Domain.AI.Planner;

/// <summary>
/// Result of executing a single plan step, returned by step executors.
/// </summary>
public sealed record StepExecutionResult
{
    /// <summary>Execution outcome status.</summary>
    public required StepExecutionStatus Status { get; init; }

    /// <summary>Step output data. Null if the step produced no output or failed.</summary>
    public string? Output { get; init; }

    /// <summary>Error message if execution failed. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Wall-clock duration of the step execution.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// HMAC-signed attestation for tool execution steps.
    /// Null for non-tool steps or when attestation could not be created.
    /// </summary>
    public ToolExecutionAttestation? Attestation { get; init; }

    /// <summary>
    /// For conditional branch steps: identifies which downstream edge to activate.
    /// Null for non-branching steps.
    /// </summary>
    public PlanStepId? ActiveEdgeTarget { get; init; }
}
