using Domain.AI.Orchestration;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Pluggable strategy for selecting which agent handles a delegated task.
/// Registered via keyed DI — default key is <c>"capability-match"</c>.
/// </summary>
public interface ISupervisorStrategy
{
    /// <summary>
    /// Selects the best agent for the given context, or null if no candidate is suitable.
    /// </summary>
    AgentSelection? SelectAgent(SupervisorDecisionContext context);
}
