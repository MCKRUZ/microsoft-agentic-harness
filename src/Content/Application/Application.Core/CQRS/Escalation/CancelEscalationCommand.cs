using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Administratively cancels a pending escalation, resolving it as denied. Used to clear stuck or
/// obsolete approval requests (agent disconnects, superseded work, governance changes). The
/// blocked agent turn — if any — is released with the denial.
/// </summary>
public sealed record CancelEscalationCommand : IRequest<Result<EscalationOutcomeSummary>>
{
    /// <summary>The pending escalation to cancel.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>Free-text reason for the cancellation, recorded in structured logs.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The identity of the administrator performing the cancellation, recorded in structured
    /// logs for accountability.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>EscalationConfig.ApproverClaimType</c>).
    /// It must never be bound from a request body, query string, or header.
    /// </remarks>
    public required string CancelledBy { get; init; }
}
