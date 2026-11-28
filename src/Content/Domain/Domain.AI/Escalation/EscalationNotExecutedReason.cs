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
    /// because none of those are distinguishable where this is reported. Reported by
    /// <c>ToolUseStepExecutor</c>, by <c>CapabilityMatchSupervisor</c> for an approved delegation the
    /// caller cancelled, and by <c>MagenticEventSubscriber</c> for an approved plan whose workflow was
    /// cancelled.
    /// </summary>
    RunCancelled,

    /// <summary>
    /// A human approved the call, then the data-classification gate blocked it. Approval only ever
    /// loosens the governor stage; the stages after it still apply, so an approved call can be refused.
    /// Reported by <c>ToolCallAdmissionPipeline</c>.
    /// </summary>
    RefusedByClassification,

    /// <summary>
    /// A human approved the call, then a host-registered tool-call observer refused it. Reported by
    /// <c>ToolCallAdmissionPipeline</c>.
    /// </summary>
    RefusedByHostRule,

    /// <summary>
    /// A human approved the call, then the loop guard halted it as a repeat with no progress. Reported by
    /// <c>ToolCallAdmissionPipeline</c>.
    /// </summary>
    RefusedByLoopGuard,

    /// <summary>
    /// A human approved the call, then call-once enforcement refused it because the same call had already
    /// happened in this conversation. Reported by <c>ToolCallAdmissionPipeline</c>.
    /// </summary>
    RefusedByCallOnce,

    /// <summary>
    /// A human approved a delegation, then no agent could take it even with the autonomy-tier floor
    /// relaxed — the approval unblocks the tier pre-filter, it cannot conjure a capable agent.
    /// Reported by <c>CapabilityMatchSupervisor</c>.
    /// </summary>
    NoAgentAvailable,

    /// <summary>
    /// A human approved a delegation, then the retry it unblocked hit the maximum delegation depth. An
    /// approved retry counts as one more level, so this is reachable when the original delegation was
    /// already one level short of the limit. Reported by <c>CapabilityMatchSupervisor</c>.
    /// </summary>
    DelegationDepthExceeded,

    /// <summary>
    /// A human approved a delegation, then no delegation slot came free before the delegation timeout —
    /// the concurrency limit was full the whole time. Reported by <c>CapabilityMatchSupervisor</c>.
    /// </summary>
    DelegationCapacityExhausted
}
