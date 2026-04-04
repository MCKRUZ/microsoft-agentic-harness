namespace Application.Common.Interfaces.Agent;

/// <summary>
/// Scoped ambient context carrying the identity of the currently executing agent.
/// Set by <c>AgentContextPropagationBehavior</c> and consumed by handlers,
/// services, and other behaviors throughout the request pipeline.
/// </summary>
/// <remarks>
/// Registered as scoped in DI. For non-agent requests, all properties remain <c>null</c>.
/// The implementation must be thread-safe — multiple concurrent agent requests may
/// execute within overlapping async contexts.
/// </remarks>
public interface IAgentExecutionContext
{
    /// <summary>Gets the current agent's unique identifier, or <c>null</c> if not in an agent context.</summary>
    string? AgentId { get; }

    /// <summary>Gets the conversation or session identifier, or <c>null</c>.</summary>
    string? ConversationId { get; }

    /// <summary>Gets the current conversation turn number, or <c>null</c>.</summary>
    int? TurnNumber { get; }

    /// <summary>
    /// Initializes the execution context with agent identity. Called once per request
    /// by <c>AgentContextPropagationBehavior</c>.
    /// </summary>
    void Initialize(string agentId, string conversationId, int turnNumber);
}
