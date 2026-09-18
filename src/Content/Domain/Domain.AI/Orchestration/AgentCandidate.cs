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

    /// <summary>
    /// The candidate's <c>AGENT.md</c> description, when known. Empty for a built-in archetype
    /// candidate (<see cref="Agents.SubagentType"/> other than <see cref="Agents.SubagentType.NamedAgent"/>),
    /// which has no manifest to describe it. Used by description-based selection strategies;
    /// tool/tier-based strategies ignore this field.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The candidate's <c>AGENT.md</c> tags, when known. Empty for a built-in archetype candidate.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The candidate's <c>AGENT.md</c> category, when known. Null for a built-in archetype candidate.</summary>
    public string? Category { get; init; }

    /// <summary>The candidate's <c>AGENT.md</c> domain, when known. Null for a built-in archetype candidate.</summary>
    public string? Domain { get; init; }
}
