namespace Domain.AI.Escalation;

/// <summary>
/// Why an approved action was never carried out. Every member names a concrete producer in this
/// codebase — a reason with nothing that reports it is dead surface an approver can never
/// actually see.
/// </summary>
public enum EscalationNotExecutedReason
{
    /// <summary>
    /// The approved action was cancelled before or during execution — e.g. a plan run was
    /// cancelled or torn down after a tool call was approved but before it completed. Broad by
    /// design: it covers an operator cancel, a caller-token cancel, and a host shutdown alike,
    /// because none of those are distinguishable from the one place this is reported —
    /// see <c>ToolUseStepExecutor</c>.
    /// </summary>
    RunCancelled
}
