namespace Domain.AI.Escalation;

/// <summary>
/// Urgency level of an escalation request. Higher values indicate greater urgency.
/// Maps to <c>EscalationPriorityConfig</c> for per-priority timeout and notification settings.
/// </summary>
public enum EscalationPriority
{
    /// <summary>Non-blocking notification. Agent may continue other work.</summary>
    Informational = 0,
    /// <summary>Agent is blocked until the escalation resolves.</summary>
    Blocking = 1,
    /// <summary>Highest urgency. All approvers notified simultaneously regardless of strategy.</summary>
    Critical = 2
}
