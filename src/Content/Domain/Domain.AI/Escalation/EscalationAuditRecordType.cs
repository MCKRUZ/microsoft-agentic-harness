namespace Domain.AI.Escalation;

/// <summary>
/// Discriminator for <see cref="EscalationAuditRecord"/> entries.
/// Determines how the <c>Payload</c> field should be deserialized.
/// </summary>
public enum EscalationAuditRecordType
{
    /// <summary>An escalation was requested.</summary>
    Request,
    /// <summary>An approver submitted a decision.</summary>
    Decision,
    /// <summary>The escalation was resolved (approved, denied, timed out, or escalated).</summary>
    Outcome
}
