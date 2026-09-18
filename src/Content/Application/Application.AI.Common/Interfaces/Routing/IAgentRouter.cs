using Domain.AI.Orchestration;

namespace Application.AI.Common.Interfaces.Routing;

/// <summary>
/// Decides which registered agent should own an incoming request, for the case where a caller
/// hasn't named one — distinct from <see cref="IModelRouter"/> (which model tier handles a
/// call) and from <c>ISupervisor</c> (which agent handles a task an already-running agent wants
/// to delegate). This is the front door: it runs before any agent has been chosen or started.
/// </summary>
public interface IAgentRouter
{
    /// <summary>
    /// Classifies <paramref name="userMessage"/> and matches it against every registered agent's
    /// <c>AGENT.md</c> description/tags/category/domain. Returns <see langword="null"/> when the
    /// intent classification is too ambiguous to route on, or when no candidate has any
    /// meaningful overlap with the request — callers should fall back to a configured default
    /// agent rather than treat a null result as an error.
    /// </summary>
    Task<AgentSelection?> RouteAsync(string userMessage, CancellationToken ct = default);
}
