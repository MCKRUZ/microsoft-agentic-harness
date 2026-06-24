namespace Domain.AI.Governance;

/// <summary>
/// The outcome of a single per-invocation governance decision made when an agent
/// attempts to call a tool during a turn. Recorded on the <see cref="GovernanceTrace"/>
/// so loop behaviour can be observed and graded independently of task outcome.
/// </summary>
public enum ToolDecisionOutcome
{
    /// <summary>The tool call was permitted to execute.</summary>
    Allowed = 0,

    /// <summary>The tool call was blocked by permission, risk, policy, or capability checks.</summary>
    Denied = 1,

    /// <summary>
    /// The tool call required human approval and was not (yet) granted. With escalation
    /// disabled this is treated as a denial; with escalation enabled it reflects a
    /// blocking approval request that was not approved.
    /// </summary>
    PendingApproval = 2,

    /// <summary>The tool call triggered a human escalation that was resolved (approved or denied) before proceeding.</summary>
    Escalated = 3
}
