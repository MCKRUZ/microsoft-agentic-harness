using Domain.AI.Governance;
using Domain.AI.Orchestration;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Coordinates multi-agent task delegation using deterministic capability matching.
/// </summary>
public interface ISupervisor
{
    /// <summary>
    /// Delegates a task to the best-fit agent selected by <see cref="ISupervisorStrategy"/>.
    /// </summary>
    /// <param name="taskDescription">Human-readable description of the task.</param>
    /// <param name="requiredCapabilities">Tool names needed for the task.</param>
    /// <param name="minimumTier">Minimum autonomy tier the selected agent must have.</param>
    /// <param name="currentDelegationDepth">Current nesting depth (0 for top-level). Enforced against MaxDelegationDepth.</param>
    /// <param name="toolOverrides">Additional tools granted for this delegation only.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DelegationResult> DelegateAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDelegationDepth = 0,
        IReadOnlyList<string>? toolOverrides = null,
        CancellationToken ct = default);

    /// <summary>Gets the latest state for a specific delegation.</summary>
    Task<DelegationRecord?> GetDelegationStatusAsync(Guid delegationId, CancellationToken ct = default);

    /// <summary>Returns all delegations in Pending or InProgress state for the current session.</summary>
    Task<IReadOnlyList<DelegationRecord>> GetActiveDelegationsAsync(CancellationToken ct = default);

    /// <summary>Triggers cancellation on a running delegation.</summary>
    Task<bool> CancelDelegationAsync(Guid delegationId, CancellationToken ct = default);
}
