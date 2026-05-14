namespace Domain.AI.Escalation;

/// <summary>
/// Controls agent behavior while an escalation is pending.
/// Configured per autonomy tier in <c>PermissionsConfig</c>.
/// </summary>
public enum EscalationWaitBehavior
{
    /// <summary>Agent pauses and awaits the escalation outcome before continuing.</summary>
    Block,
    /// <summary>Agent continues processing other work; escalation resolves asynchronously.</summary>
    QueueAndContinue
}
