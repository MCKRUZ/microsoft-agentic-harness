using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Submits the caller's approve/deny decision on a pending escalation. The service records the
/// decision, evaluates the approval strategy (AnyOf/AllOf/Quorum), and — when this decision
/// resolves the escalation — releases the agent turn blocked on it in the same process.
/// </summary>
public sealed record SubmitEscalationDecisionCommand
    : IRequest<Result<SubmitEscalationDecisionResult>>
{
    /// <summary>The escalation being decided.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>
    /// The deciding approver's identity, compared against the escalation's roster by the
    /// service using <c>ApproverNames.Comparer</c>.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>EscalationConfig.ApproverClaimType</c>).
    /// It must never be bound from a request body, query string, or header — the wire DTO
    /// deliberately has no approver-name field, so a caller cannot decide as someone else.
    /// </remarks>
    public required string ApproverName { get; init; }

    /// <summary>Whether the caller approves the escalated action.</summary>
    public required bool Approve { get; init; }

    /// <summary>Optional free-text reason recorded with the decision. Especially useful for denials.</summary>
    public string? Reason { get; init; }
}
