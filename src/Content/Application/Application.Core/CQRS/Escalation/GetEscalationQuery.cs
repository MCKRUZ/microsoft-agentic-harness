using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Reads a single escalation: its pending summary while undecided, or its resolved outcome after
/// a verdict. This is the poll target after a decision returns <c>DecisionRecorded</c> (202).
/// </summary>
/// <remarks>
/// <b>Roster privacy:</b> a caller not on the escalation's approver roster receives
/// <c>NotFound</c> — indistinguishable from an unknown id — for pending <em>and</em> resolved
/// escalations alike. Resolved outcomes carry the originating roster forward
/// (<c>EscalationOutcome.Approvers</c>) precisely so this check survives the pending request
/// being discarded on resolution.
/// </remarks>
public sealed record GetEscalationQuery : IRequest<Result<EscalationDetail>>
{
    /// <summary>The escalation to read.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>
    /// The caller's approver identity, used for the pending-state roster check.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>EscalationConfig.ApproverClaimType</c>).
    /// It must never be bound from a request body, query string, or header.
    /// </remarks>
    public required string ApproverName { get; init; }
}
