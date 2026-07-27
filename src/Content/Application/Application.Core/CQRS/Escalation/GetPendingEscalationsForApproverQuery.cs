using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Lists the pending escalations whose approver roster contains the caller. Roster matching is
/// case-insensitive via <c>ApproverNames.Comparer</c> — the single comparison authority shared
/// with the decide path — so an identity that can decide an escalation always also sees it here.
/// </summary>
public sealed record GetPendingEscalationsForApproverQuery
    : IRequest<Result<IReadOnlyList<EscalationSummary>>>
{
    /// <summary>
    /// The caller's approver identity.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>EscalationConfig.ApproverClaimType</c>).
    /// It must never be bound from a request body, query string, or header — no wire DTO
    /// carries an approver-name field by design, so a caller cannot assert an identity their
    /// token does not prove.
    /// </remarks>
    public required string ApproverName { get; init; }
}
