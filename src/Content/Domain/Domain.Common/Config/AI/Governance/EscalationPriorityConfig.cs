namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Per-priority-level overrides for escalation behavior.
/// Each entry in <see cref="EscalationConfig.PriorityLevels"/> maps to one of these.
/// </summary>
public class EscalationPriorityConfig
{
    /// <summary>Override timeout (in seconds) for this priority level.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// When true, escalation is non-blocking (informational only).
    /// The agent continues processing while the escalation resolves asynchronously.
    /// </summary>
    public bool Async { get; set; }

    /// <summary>
    /// When true, all approvers are notified simultaneously regardless of strategy ordering.
    /// Typically used for <c>Critical</c> priority.
    /// </summary>
    public bool EscalateToAll { get; set; }
}
