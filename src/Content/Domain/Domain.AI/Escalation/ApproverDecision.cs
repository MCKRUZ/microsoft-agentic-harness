namespace Domain.AI.Escalation;

/// <summary>
/// A single approver's response to an escalation request.
/// Collected by the escalation service and evaluated by the approval strategy.
/// </summary>
public sealed record ApproverDecision
{
    /// <summary>Identifier of the approver (user name, role, or service principal).</summary>
    public required string ApproverName { get; init; }

    /// <summary>The approver's verdict.</summary>
    public required ApproverVerdict Verdict { get; init; }

    /// <summary>
    /// Optional reason for the decision. Especially useful for denials.
    /// Operator-facing only — like every other free-text reason in this subsystem, never relayed
    /// to the model. See <see cref="Instructions"/> for the field that deliberately is.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Steering instructions for the agent's next attempt, meaningful only when
    /// <see cref="Verdict"/> is <see cref="ApproverVerdict.Revise"/>. Unlike <see cref="Reason"/>,
    /// this field is <em>intended</em> to reach the model, and every consumer that relays it must
    /// sanitize it, attribute it explicitly as human-authored feedback, and delimit it from
    /// surrounding content — never present it as a system directive. This type does not enforce
    /// any of that itself; a consumer that reads this field and forwards it verbatim is not
    /// honoring the contract.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>When the approver responded.</summary>
    public required DateTimeOffset RespondedAt { get; init; }
}
