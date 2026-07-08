using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// Shared roster-scoping logic for the <see cref="IApprovalStrategy"/> implementations.
/// Filters raw approver decisions down to the request's declared approver roster and
/// computes which listed approvers are still pending.
/// </summary>
/// <remarks>
/// Centralizing this here removes the previously copy-pasted deduplication helper from
/// each strategy and, critically, guarantees every strategy applies the same membership
/// filter. Without it, <c>AnyOf</c> and <c>AllOf</c> counted votes from identities outside
/// <see cref="EscalationRequest.Approvers"/>, letting a non-roster caller approve (or force
/// a denial on) an escalation.
/// </remarks>
internal static class ApproverRoster
{
    /// <summary>
    /// Scopes the collected decisions to the request's approver roster.
    /// </summary>
    /// <param name="request">The escalation request carrying the authoritative approver roster.</param>
    /// <param name="decisions">All decisions collected so far, including any from non-roster identities.</param>
    /// <returns>
    /// The roster-filtered decisions (deduplicated to each approver's earliest response) and the
    /// listed approvers who have not yet responded.
    /// </returns>
    public static RosterScopedDecisions Scope(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions)
    {
        var approverSet = request.Approvers.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only count decisions from identities actually listed as approvers, collapsing
        // repeat votes to the earliest response so a later flip cannot rewrite the outcome.
        var rostered = decisions
            .Where(d => approverSet.Contains(d.ApproverName))
            .GroupBy(d => d.ApproverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MinBy(d => d.RespondedAt)!)
            .ToArray();

        var respondedNames = rostered
            .Select(d => d.ApproverName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = request.Approvers
            .Where(a => !respondedNames.Contains(a))
            .ToArray();

        return new RosterScopedDecisions(rostered, pending);
    }
}

/// <summary>
/// The result of <see cref="ApproverRoster.Scope"/>: roster-filtered decisions and the
/// listed approvers still awaiting a response.
/// </summary>
/// <param name="Decisions">Decisions from listed approvers only, deduplicated to earliest response.</param>
/// <param name="Pending">Listed approvers who have not yet responded.</param>
internal readonly record struct RosterScopedDecisions(
    IReadOnlyList<ApproverDecision> Decisions,
    IReadOnlyList<string> Pending);
