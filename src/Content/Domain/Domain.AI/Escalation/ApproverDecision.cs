namespace Domain.AI.Escalation;

/// <summary>
/// A single approver's response to an escalation request.
/// Collected by the escalation service and evaluated by the approval strategy.
/// </summary>
public sealed record ApproverDecision
{
    /// <summary>Identifier of the approver (user name, role, or service principal).</summary>
    public required string ApproverName { get; init; }

    /// <summary>Whether the approver granted approval.</summary>
    public required bool Approved { get; init; }

    /// <summary>Optional reason for the decision. Especially useful for denials.</summary>
    public string? Reason { get; init; }

    /// <summary>When the approver responded.</summary>
    public required DateTimeOffset RespondedAt { get; init; }
}
