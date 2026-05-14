namespace Domain.AI.Escalation;

/// <summary>
/// Strategy for evaluating multiple approver decisions.
/// Used as the keyed DI discriminator for <c>IApprovalStrategy</c> resolution.
/// </summary>
public enum ApprovalStrategyType
{
    /// <summary>First approver response wins. Fastest resolution.</summary>
    AnyOf,
    /// <summary>All designated approvers must approve. A single denial immediately denies.</summary>
    AllOf,
    /// <summary>N-of-M approvers must agree. Requires <c>QuorumThreshold</c> on the request.</summary>
    Quorum
}
