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

    /// <summary>
    /// Delegates a task directly to a specific, named <c>AGENT.md</c>-registered peer agent (#518),
    /// bypassing <see cref="ISupervisorStrategy"/>'s scoring entirely — the caller already knows
    /// exactly which peer it wants, typically because <c>PeerAgentContextProvider</c> told it. Unlike
    /// <see cref="DelegateAsync"/>, there is no minimum-tier floor to enforce: an <c>AgentDefinition</c>
    /// carries no autonomy tier of its own to check against, and the caller has already made an
    /// informed, specific choice rather than asking to be matched to one.
    /// </summary>
    /// <param name="targetAgentId">
    /// The peer's <c>AgentDefinition.Id</c>. Must be registered and must not be
    /// <paramref name="callingAgentId"/> — both are refused with a caller-facing failure, never a
    /// thrown exception, matching <see cref="DelegateAsync"/>'s existing "no capable agent" refusal
    /// shape.
    /// </param>
    /// <param name="taskDescription">Human-readable description of the task.</param>
    /// <param name="callingAgentId">
    /// The delegating agent's own id, for self-exclusion — <see langword="null"/> when the caller
    /// could not resolve its own identity (no ambient request scope), in which case self-exclusion is
    /// skipped rather than refusing the call: reaching one's own id by name is wasteful, not unsafe,
    /// and is still bounded by <paramref name="currentDelegationDepth"/> like any other delegation.
    /// </param>
    /// <param name="currentDelegationDepth">Current nesting depth (0 for top-level). Enforced against MaxDelegationDepth.</param>
    /// <param name="toolOverrides">
    /// Unused on this path — a named peer agent already carries its own skill-derived tool set, unlike
    /// a built-in profile's restrictive allowlist. Accepted for parity with <see cref="DelegateAsync"/>
    /// and recorded for audit, but does not affect which tools the delegated agent gets.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DelegationResult> DelegateToNamedAgentAsync(
        string targetAgentId,
        string taskDescription,
        string? callingAgentId,
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
