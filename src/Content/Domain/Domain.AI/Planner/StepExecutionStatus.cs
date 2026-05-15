namespace Domain.AI.Planner;

/// <summary>
/// Represents the state machine for plan step execution lifecycle.
/// Transitions: Pending → Ready → Running → Completed | Failed | Skipped.
/// Blocked is entered from Ready when awaiting external input (e.g., human gate).
/// </summary>
public enum StepExecutionStatus
{
    /// <summary>Not yet eligible for execution; dependencies not met.</summary>
    Pending,

    /// <summary>All dependencies met; awaiting scheduler pickup.</summary>
    Ready,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished with error; may be retried per <see cref="RetryPolicy"/>.</summary>
    Failed,

    /// <summary>Skipped due to upstream failure or conditional branch not taken.</summary>
    Skipped,

    /// <summary>Waiting on external input such as human gate approval.</summary>
    Blocked
}
