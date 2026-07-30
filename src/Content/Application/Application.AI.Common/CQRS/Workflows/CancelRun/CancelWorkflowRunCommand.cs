using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Workflows.CancelRun;

/// <summary>
/// Stops a run the caller started, and withdraws any approval it was waiting on.
/// </summary>
/// <remarks>
/// Carries the caller's identity for the same reason every other run request does: the store answers
/// as though another owner's run does not exist, and a command that omitted the caller could stop
/// anyone's work.
/// </remarks>
public sealed record CancelWorkflowRunCommand : IRequest<Result<CancelWorkflowRunResult>>
{
    /// <summary>Identifier of the workflow the run belongs to.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>Identifier of the run to stop.</summary>
    public required string JobId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, resolved from its token, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The cancelling caller's approver identity, resolved by the transport from its token. Null when
    /// the host could not establish one.
    /// </summary>
    /// <remarks>
    /// Recorded as who withdrew the run's pending approvals. Distinct from <see cref="OwnerId"/>, which
    /// is the same person under a different, immutable claim: the withdrawal lands beside approver
    /// names in the escalation audit, where an owner id reads differently from every other row. Falls
    /// back to <see cref="OwnerId"/> when absent, because a withdrawal must always be attributable to
    /// someone — an unattributed one is worse than an awkwardly-shaped one.
    /// </remarks>
    public string? CancellerApproverName { get; init; }
}
