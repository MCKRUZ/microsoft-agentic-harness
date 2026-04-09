namespace Application.AI.Common.Interfaces.Compaction;

/// <summary>
/// Tracks compaction state per agent for the auto-compact system.
/// Implements a circuit breaker that stops attempting compaction after
/// consecutive failures.
/// </summary>
public interface IAutoCompactStateMachine
{
    /// <summary>Records a successful compaction for the agent.</summary>
    /// <param name="agentId">The agent identifier.</param>
    void RecordSuccess(string agentId);

    /// <summary>Records a failed compaction attempt for the agent.</summary>
    /// <param name="agentId">The agent identifier.</param>
    void RecordFailure(string agentId);

    /// <summary>Returns true if the circuit breaker has tripped (too many consecutive failures).</summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <returns>True if the circuit breaker is open and compaction should not be attempted.</returns>
    bool IsCircuitBroken(string agentId);

    /// <summary>Gets the number of consecutive failures for the agent.</summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <returns>The consecutive failure count, or 0 if no failures recorded.</returns>
    int GetConsecutiveFailures(string agentId);
}
