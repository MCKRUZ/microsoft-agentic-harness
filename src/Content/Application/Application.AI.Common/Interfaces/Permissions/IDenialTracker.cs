using Domain.AI.Permissions;

namespace Application.AI.Common.Interfaces.Permissions;

/// <summary>
/// Tracks permission denials per agent and rate-limits repeated attempts.
/// When the same tool+operation is denied N times (configurable via
/// <c>AppConfig.AI.Permissions.DenialRateLimitThreshold</c>), the tracker
/// auto-escalates to a hard deny without re-evaluating rules.
/// </summary>
public interface IDenialTracker
{
    /// <summary>
    /// Records a permission denial for the specified agent, tool, and operation.
    /// </summary>
    /// <param name="agentId">The agent whose denial is being recorded.</param>
    /// <param name="toolName">The tool that was denied.</param>
    /// <param name="operation">The specific operation denied, or null for tool-level denials.</param>
    void RecordDenial(string agentId, string toolName, string? operation = null);

    /// <summary>
    /// Returns true if the agent has been denied this tool+operation too many times
    /// and should be automatically denied without rule evaluation.
    /// </summary>
    /// <param name="agentId">The agent to check.</param>
    /// <param name="toolName">The tool to check.</param>
    /// <param name="operation">The specific operation to check, or null for tool-level checks.</param>
    /// <returns>True if the denial count has reached or exceeded the threshold.</returns>
    bool IsRateLimited(string agentId, string toolName, string? operation = null);

    /// <summary>
    /// Gets all denial records for an agent.
    /// </summary>
    /// <param name="agentId">The agent whose denial records to retrieve.</param>
    /// <returns>A snapshot of all denial records for the agent.</returns>
    IReadOnlyList<DenialRecord> GetDenials(string agentId);

    /// <summary>
    /// Resets all denial tracking for an agent (e.g., at session start).
    /// </summary>
    /// <param name="agentId">The agent whose denial records to clear.</param>
    void Reset(string agentId);
}
