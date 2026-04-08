using Domain.AI.A2A;

namespace Application.AI.Common.Interfaces.A2A;

/// <summary>
/// Provides Agent-to-Agent protocol capabilities: agent card publishing,
/// remote agent discovery, and task delegation.
/// </summary>
public interface IA2AAgentHost
{
    /// <summary>Gets the local agent card for this harness instance.</summary>
    AgentCard GetAgentCard();

    /// <summary>Sends a task description to a remote A2A agent and returns its response.</summary>
    Task<string> SendTaskAsync(string agentUrl, string taskDescription, CancellationToken cancellationToken = default);

    /// <summary>Discovers available A2A agents from configured remote endpoints.</summary>
    Task<IReadOnlyList<AgentCard>> DiscoverAgentsAsync(CancellationToken cancellationToken = default);
}
