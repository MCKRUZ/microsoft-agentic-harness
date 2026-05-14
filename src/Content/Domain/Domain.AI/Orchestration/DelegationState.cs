namespace Domain.AI.Orchestration;

/// <summary>
/// Lifecycle state of a delegation. State transitions are append-only —
/// new records are written for each transition rather than mutating existing ones.
/// </summary>
public enum DelegationState
{
    /// <summary>Delegation created, not yet started.</summary>
    Pending,
    /// <summary>Agent is actively executing the delegated task.</summary>
    InProgress,
    /// <summary>Agent completed the task successfully.</summary>
    Completed,
    /// <summary>Agent failed (includes autonomy exceeded, timeout, exception).</summary>
    Failed,
    /// <summary>Delegation explicitly cancelled by supervisor or caller.</summary>
    Cancelled
}
