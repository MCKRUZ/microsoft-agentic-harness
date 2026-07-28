using Domain.AI.Changes;

namespace Presentation.Common.ChangeProposals;

// The wire-contract records below are deliberately colocated with the controller's folder (the
// EscalationsController precedent): they are this endpoint set's HTTP contract, not shared
// application models. None of the request bodies carries a reviewer-identity field — the
// deciding identity always comes from the authenticated token's configured claim, so it cannot
// be spoofed through the body.

/// <summary>Request body for <c>POST /api/change-proposals/{id}/approve</c>.</summary>
/// <param name="Reason">Optional free-text reason recorded in the gate-history audit entry (max 2000 characters).</param>
/// <remarks>
/// Deliberately carries no reviewer-id field: the approving identity always comes from the
/// authenticated token's configured claim, so it cannot be spoofed through the body.
/// </remarks>
public sealed record ApproveChangeProposalRequest(string? Reason = null);

/// <summary>Request body for <c>POST /api/change-proposals/{id}/reject</c>.</summary>
/// <param name="Reason">Required free-text reason (max 2000 characters) — it surfaces in the audit trail and back to the submitting agent.</param>
public sealed record RejectChangeProposalRequest(string Reason);

/// <summary>Request body for <c>POST /api/change-proposals/{id}/cancel</c>.</summary>
/// <param name="Reason">Optional free-text reason recorded in the gate-history audit entry (max 2000 characters).</param>
public sealed record CancelChangeProposalRequest(string? Reason = null);

/// <summary>
/// One change proposal in a <c>GET /api/change-proposals</c> listing — the identifying and
/// triage fields a reviewer scans to pick a proposal, without the full diff or gate history.
/// </summary>
public sealed record ChangeProposalSummaryResponse
{
    /// <summary>The proposal's deterministic id — the route parameter for the detail and decision endpoints.</summary>
    public required string Id { get; init; }

    /// <summary>A short human-readable summary of the change.</summary>
    public required string Summary { get; init; }

    /// <summary>The current lifecycle state.</summary>
    public required ChangeProposalStatus Status { get; init; }

    /// <summary>The discriminator of the target the change will apply to (git repo, Kubernetes resource, IaC deployment, …).</summary>
    public required ChangeTargetKind TargetKind { get; init; }

    /// <summary>The target's short human-readable identifier — repo url, resource name, deployment name.</summary>
    public required string TargetDisplayName { get; init; }

    /// <summary>The submitter's estimate of the change's impact radius.</summary>
    public required BlastRadius BlastRadius { get; init; }

    /// <summary>The stable id of the agent identity that submitted the proposal.</summary>
    public required string SubmittedByAgentId { get; init; }

    /// <summary>The wall-clock submission time.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>Projects a domain <see cref="ChangeProposal"/> into the summary wire shape.</summary>
    /// <param name="proposal">The proposal to project.</param>
    public static ChangeProposalSummaryResponse From(ChangeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new ChangeProposalSummaryResponse
        {
            Id = proposal.Id,
            Summary = proposal.Summary,
            Status = proposal.Status,
            TargetKind = proposal.Target.Kind,
            TargetDisplayName = proposal.Target.DisplayName,
            BlastRadius = proposal.BlastRadius,
            SubmittedByAgentId = proposal.SubmittedBy.Id,
            SubmittedAt = proposal.SubmittedAt
        };
    }
}

/// <summary>
/// The full reviewable view of one change proposal — everything a reviewer needs to reach a
/// decision: the summary fields plus the ordered diff, the required gate pipeline, and the
/// append-only gate history (including who decided what, when). Returned by
/// <c>GET /api/change-proposals/{id}</c> and echoed by the decision endpoints as the post-decision
/// snapshot.
/// </summary>
/// <remarks>
/// A projection exists (rather than serializing <see cref="ChangeProposal"/> directly) because
/// the domain aggregate's <c>Target</c> property is an abstract class hierarchy without JSON
/// polymorphism configuration — System.Text.Json would silently truncate it to the base-class
/// properties. The projection makes the wire shape explicit and stable. The nested
/// <see cref="ChangeEdit"/> and <see cref="GateDecision"/> records are simple primitive-only
/// value records and are reused directly.
/// </remarks>
public sealed record ChangeProposalDetailResponse
{
    /// <summary>The proposal's deterministic id.</summary>
    public required string Id { get; init; }

    /// <summary>A short human-readable summary of the change.</summary>
    public required string Summary { get; init; }

    /// <summary>The current lifecycle state.</summary>
    public required ChangeProposalStatus Status { get; init; }

    /// <summary>The discriminator of the target the change will apply to.</summary>
    public required ChangeTargetKind TargetKind { get; init; }

    /// <summary>The target's short human-readable identifier.</summary>
    public required string TargetDisplayName { get; init; }

    /// <summary>The submitter's estimate of the change's impact radius.</summary>
    public required BlastRadius BlastRadius { get; init; }

    /// <summary>The stable id of the agent identity that submitted the proposal.</summary>
    public required string SubmittedByAgentId { get; init; }

    /// <summary>The wall-clock submission time.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>The ordered gate keys the orchestrator must run for this proposal.</summary>
    public required IReadOnlyList<string> RequiredGates { get; init; }

    /// <summary>The ordered list of bounded edits the diff comprises.</summary>
    public required IReadOnlyList<ChangeEdit> Diff { get; init; }

    /// <summary>The append-only gate-decision audit history, in evaluation order. Decision entries carry the deciding reviewer's token-stamped identity.</summary>
    public required IReadOnlyList<GateDecision> History { get; init; }

    /// <summary>Projects a domain <see cref="ChangeProposal"/> into the detail wire shape.</summary>
    /// <param name="proposal">The proposal to project.</param>
    public static ChangeProposalDetailResponse From(ChangeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new ChangeProposalDetailResponse
        {
            Id = proposal.Id,
            Summary = proposal.Summary,
            Status = proposal.Status,
            TargetKind = proposal.Target.Kind,
            TargetDisplayName = proposal.Target.DisplayName,
            BlastRadius = proposal.BlastRadius,
            SubmittedByAgentId = proposal.SubmittedBy.Id,
            SubmittedAt = proposal.SubmittedAt,
            RequiredGates = proposal.RequiredGates,
            Diff = proposal.Diff,
            History = proposal.History
        };
    }
}
