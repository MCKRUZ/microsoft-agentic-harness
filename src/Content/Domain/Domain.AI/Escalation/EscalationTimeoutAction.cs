namespace Domain.AI.Escalation;

/// <summary>
/// Action taken when an escalation request expires without sufficient approver responses.
/// </summary>
public enum EscalationTimeoutAction
{
    /// <summary>Deny the action on timeout.</summary>
    Deny,
    /// <summary>Deny the action and escalate to a higher authority tier.</summary>
    DenyAndEscalate,
    /// <summary>
    /// Auto-approve the action on timeout (use with caution).
    /// </summary>
    /// <remarks>
    /// SECURITY: Should never be paired with <see cref="EscalationPriority.Critical"/>.
    /// Enforce via FluentValidation at the Application layer.
    /// </remarks>
    Approve,
    /// <summary>Escalate to a higher authority tier without denying.</summary>
    Escalate
}
