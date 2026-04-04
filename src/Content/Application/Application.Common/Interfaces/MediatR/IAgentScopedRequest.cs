namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for requests executing within an agent context.
/// Consumed by <c>AgentContextPropagationBehavior</c> to push agent identity
/// onto the logging scope and <see cref="Agent.IAgentExecutionContext"/>.
/// </summary>
public interface IAgentScopedRequest
{
    /// <summary>Gets the agent's unique identifier.</summary>
    string AgentId { get; }

    /// <summary>Gets the conversation or session identifier.</summary>
    string ConversationId { get; }

    /// <summary>Gets the current conversation turn number.</summary>
    int TurnNumber { get; }
}
