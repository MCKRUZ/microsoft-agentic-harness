namespace Domain.AI.A2A;

/// <summary>
/// Represents an A2A agent card describing an agent's capabilities for discovery.
/// Follows the Agent-to-Agent protocol specification for agent metadata exchange.
/// </summary>
public record AgentCard
{
    /// <summary>Agent's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Description of the agent's purpose and capabilities.</summary>
    public required string Description { get; init; }

    /// <summary>The agent's A2A endpoint URL.</summary>
    public string? Url { get; init; }

    /// <summary>List of capability identifiers this agent supports.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Skill IDs available on this agent.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Agent version string.</summary>
    public string? Version { get; init; }
}
