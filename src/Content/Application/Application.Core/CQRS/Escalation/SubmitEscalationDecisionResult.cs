using Domain.AI.Escalation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// The application-layer projection of an <see cref="EscalationDecisionResult"/>: the
/// discriminated <see cref="Status"/> plus the wire-safe outcome summary when — and only when —
/// this decision resolved the escalation. The controller maps <see cref="Status"/> to HTTP per
/// the mapping documented on <see cref="EscalationDecisionStatus"/> (404/403/202/200).
/// </summary>
public sealed record SubmitEscalationDecisionResult
{
    /// <summary>What happened to the submitted decision.</summary>
    public required EscalationDecisionStatus Status { get; init; }

    /// <summary>
    /// The final verdict. Non-null if and only if <see cref="Status"/> is
    /// <see cref="EscalationDecisionStatus.Resolved"/>.
    /// </summary>
    public EscalationOutcomeSummary? Outcome { get; init; }

    /// <summary>Projects a domain <see cref="EscalationDecisionResult"/> to the wire-safe shape.</summary>
    /// <param name="result">The service's discriminated decision result. Must not be null.</param>
    public static SubmitEscalationDecisionResult FromDecisionResult(EscalationDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SubmitEscalationDecisionResult
        {
            Status = result.Status,
            Outcome = result.Outcome is null
                ? null
                : EscalationOutcomeSummary.FromOutcome(result.Outcome)
        };
    }
}
