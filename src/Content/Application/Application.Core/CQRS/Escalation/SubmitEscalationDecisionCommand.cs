using Domain.AI.Escalation;
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

    /// <summary>
    /// Whether the caller approves the escalated action. Kept alongside <see cref="Verdict"/> for
    /// callers written before the three-way verdict existed — a legacy caller sending only this
    /// field keeps working, mapped to Approve/Deny. When both are present they must agree; the
    /// handler's validator rejects a contradiction rather than silently picking one.
    /// </summary>
    public required bool Approve { get; init; }

    /// <summary>
    /// The caller's verdict (#321). Optional so a legacy caller sending only <see cref="Approve"/>
    /// keeps working; when present, resolved in preference to <see cref="Approve"/>.
    /// </summary>
    public ApproverVerdict? Verdict { get; init; }

    /// <summary>Optional free-text reason recorded with the decision. Especially useful for denials.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Steering instructions for the agent's next attempt, required when <see cref="Verdict"/> is
    /// <see cref="ApproverVerdict.Revise"/>. Unlike <see cref="Reason"/>, this text is designed to
    /// reach the model, sanitized and attributed as human-authored feedback.
    /// </summary>
    public string? Instructions { get; init; }
}
