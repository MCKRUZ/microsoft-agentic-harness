namespace Domain.AI.Escalation;

/// <summary>
/// How an escalation was ultimately resolved. Used for audit records and OTel metric tags.
/// </summary>
public enum EscalationResolutionType
{
    /// <summary>Approved by sufficient approvers per the strategy.</summary>
    Approved,
    /// <summary>Denied by an approver or by strategy rules.</summary>
    Denied,
    /// <summary>No sufficient response within the timeout window.</summary>
    TimedOut,
    /// <summary>Forwarded to a higher authority tier.</summary>
    Escalated,
    /// <summary>
    /// An approver asked the agent to revise its approach. Not an approval — consumers that only
    /// check <see cref="EscalationOutcome.IsApproved"/> see this as not-approved, identically to
    /// <see cref="Denied"/>, with zero code change required. A consumer that wants to act on the
    /// revision must explicitly check for this resolution type.
    /// </summary>
    Revised
}
