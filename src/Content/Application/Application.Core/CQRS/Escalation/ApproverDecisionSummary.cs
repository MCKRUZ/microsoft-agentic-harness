using Domain.AI.Escalation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// A wire-safe projection of a single <see cref="ApproverDecision"/> inside a resolved
/// escalation outcome: who decided, which way, why, and when.
/// </summary>
public sealed record ApproverDecisionSummary
{
    /// <summary>Identifier of the approver, exactly as recorded (original casing preserved).</summary>
    public required string ApproverName { get; init; }

    /// <summary>
    /// Whether the approver granted approval. Derived from <see cref="Verdict"/> — true only for
    /// <see cref="ApproverVerdict.Approve"/>; false for both a denial and a revision. Kept for
    /// clients written before <see cref="Verdict"/> existed.
    /// </summary>
    public required bool Approved { get; init; }

    /// <summary>The approver's verdict.</summary>
    public ApproverVerdict? Verdict { get; init; }

    /// <summary>Optional reason the approver supplied with the decision.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The approver's steering instructions, when <see cref="Verdict"/> is
    /// <see cref="ApproverVerdict.Revise"/>. Shown to the next reviewer so a second round is
    /// never indistinguishable from being asked the same question twice.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>When the approver responded.</summary>
    public required DateTimeOffset RespondedAt { get; init; }

    /// <summary>Projects a domain <see cref="ApproverDecision"/> to the wire-safe shape.</summary>
    /// <param name="decision">The recorded decision to project. Must not be null.</param>
    public static ApproverDecisionSummary FromDecision(ApproverDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new ApproverDecisionSummary
        {
            ApproverName = decision.ApproverName,
            Approved = decision.IsApproved,
            Verdict = decision.Verdict,
            Reason = decision.Reason,
            Instructions = decision.Instructions,
            RespondedAt = decision.RespondedAt
        };
    }
}
