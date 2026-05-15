namespace Domain.AI.Planner;

/// <summary>
/// Determines how many approvers must respond to satisfy a human gate.
/// </summary>
public enum ApprovalStrategy
{
    /// <summary>Any single approver can satisfy the gate.</summary>
    AnyOf,

    /// <summary>All designated approvers must approve.</summary>
    AllOf,

    /// <summary>A majority of designated approvers must approve.</summary>
    Quorum
}
