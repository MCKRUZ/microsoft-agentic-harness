namespace Domain.AI.Escalation;

/// <summary>
/// Risk level of an agent action, derived from the matched governance rule.
/// Used in <see cref="EscalationRequest"/> and OTel metric tags.
/// </summary>
public enum RiskLevel
{
    /// <summary>Minimal risk. Typically informational escalations.</summary>
    Low = 0,
    /// <summary>Moderate risk. Standard approval workflow.</summary>
    Medium = 1,
    /// <summary>Elevated risk. May trigger stricter approval strategies.</summary>
    High = 2,
    /// <summary>Highest risk. All approvers notified, shortest timeout window.</summary>
    Critical = 3
}
