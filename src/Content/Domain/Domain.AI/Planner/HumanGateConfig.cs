using Domain.AI.Escalation;

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

    /// <summary>
    /// Identities (user IDs or group names) required to approve this gate.
    /// </summary>
    /// <remarks>
    /// Defaults to an empty list deliberately: there is no safe placeholder approver.
    /// A previous default of <c>["default-approver"]</c> masked the "author forgot to set
    /// Approvers" misconfiguration behind a non-empty list — the resulting escalation matched
    /// no real approver in <c>GetPendingEscalationsAsync</c>'s <c>Approvers.Contains(...)</c>
    /// filter, so the gate became invisible to every pending queue and silently stalled the
    /// plan until timeout. An empty list is an honest, validatable sentinel: validation
    /// (a <c>NotEmpty</c> rule on <c>HumanGateConfig.Approvers</c>) can reject it loudly at
    /// construction/validation time instead of letting a dead gate reach runtime. Callers
    /// (including LLM plan-output mapping) must supply at least one real approver identity.
    /// </remarks>
    public IReadOnlyList<string> Approvers { get; init; } = [];

    /// <summary>Risk level associated with this gate, used to prioritize escalation routing.</summary>
    public RiskLevel RiskLevel { get; init; } = RiskLevel.Medium;

    /// <summary>Maximum time to wait for approval before the gate times out.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);
}
