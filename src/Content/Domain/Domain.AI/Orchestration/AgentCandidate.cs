using Domain.AI.Agents;
using Domain.AI.Governance;

namespace Domain.AI.Orchestration;

/// <summary>
/// Describes one candidate agent for delegation selection.
/// </summary>
public sealed record AgentCandidate
{
    /// <summary>Unique agent identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The built-in agent type profile.</summary>
    public required SubagentType AgentType { get; init; }

    /// <summary>The trust tier assigned to this agent.</summary>
    public required AutonomyLevel AutonomyLevel { get; init; }

    /// <summary>Tools available to this agent.</summary>
    public required IReadOnlyList<string> AvailableTools { get; init; }
}
