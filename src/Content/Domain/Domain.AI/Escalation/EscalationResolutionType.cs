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
    Escalated
}
