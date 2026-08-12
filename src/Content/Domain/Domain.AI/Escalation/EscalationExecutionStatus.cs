namespace Domain.AI.Escalation;

/// <summary>
/// What happened when an approved action was actually carried out. Reported after
/// <see cref="EscalationOutcome.IsApproved"/> is true, closing the gap where an approver learns
/// they approved something but never learns whether it then worked.
/// </summary>
public enum EscalationExecutionStatus
{
    /// <summary>The approved action ran and completed successfully.</summary>
    Succeeded,
    /// <summary>The approved action ran and failed. See the record's failure reason.</summary>
    Failed,
    /// <summary>
    /// The approved action never ran — see the record's not-executed reason for why.
    /// </summary>
    NeverExecuted
}
