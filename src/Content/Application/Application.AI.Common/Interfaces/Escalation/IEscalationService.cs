using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Orchestrates the escalation lifecycle: creation, notification dispatch,
/// approval tracking, timeout management, and outcome resolution.
/// </summary>
/// <remarks>
/// Two consumption modes are supported:
/// <list type="bullet">
///   <item><description><see cref="RequestEscalationAsync"/> -- blocking; caller awaits the human decision.</description></item>
///   <item><description><see cref="QueueEscalationAsync"/> -- non-blocking; returns an ID for later polling.</description></item>
/// </list>
/// The mode is selected by the agent's <c>EscalationWaitBehavior</c> (Block vs. QueueAndContinue),
/// resolved from the autonomy tier policy.
/// </remarks>
public interface IEscalationService
{
    /// <summary>
    /// Creates an escalation and blocks until a human decision resolves it or the timeout expires.
    /// </summary>
    Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Creates an escalation without blocking. Returns the escalation ID for later polling.
    /// </summary>
    Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Submits an approver's decision. Returns the final outcome if this decision resolves
    /// the escalation (per the approval strategy), or null if the escalation is still pending.
    /// </summary>
    Task<EscalationOutcome?> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);

    /// <summary>
    /// Returns the pending escalation request, or null if resolved or unknown.
    /// </summary>
    Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Returns all pending escalations assigned to a specific approver.
    /// </summary>
    Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct);

    /// <summary>
    /// Explicitly cancels a pending escalation. Used for agent disconnects,
    /// admin force-resolve, or governance context changes.
    /// </summary>
    Task<EscalationOutcome> CancelEscalationAsync(Guid escalationId, string reason, CancellationToken ct);
}
