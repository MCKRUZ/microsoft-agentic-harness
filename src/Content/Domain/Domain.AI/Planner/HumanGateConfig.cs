namespace Domain.AI.Planner;

/// <summary>
/// Configuration for a human approval gate. Pauses plan execution and
/// queues an escalation until the required approvals are received or the gate times out.
/// </summary>
public sealed record HumanGateConfig : StepConfiguration
{
    /// <summary>Message displayed to the human approver explaining what needs approval.</summary>
    public required string EscalationMessage { get; init; }

    /// <summary>Determines how many approvers must respond to satisfy this gate.</summary>
    public required ApprovalStrategy ApprovalStrategy { get; init; }

    /// <summary>Maximum time to wait for approval before the gate times out.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);
}
