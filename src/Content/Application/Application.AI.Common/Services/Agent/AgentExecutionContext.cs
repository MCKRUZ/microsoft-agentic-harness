using Application.AI.Common.Interfaces.Agent;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// Scoped ambient context carrying the identity of the currently executing agent.
/// Set once per request by <see cref="AgentContextPropagationBehavior{TRequest, TResponse}"/>
/// and consumed by downstream behaviors, handlers, and services.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> in DI — each MediatR request scope gets its own instance.
/// Properties remain <c>null</c> for non-agent requests.
/// </remarks>
public sealed class AgentExecutionContext : IAgentExecutionContext
{
    private bool _initialized;

    /// <inheritdoc />
    public string? AgentId { get; private set; }

    /// <inheritdoc />
    public string? ConversationId { get; private set; }

    /// <inheritdoc />
    public int? TurnNumber { get; private set; }

    /// <inheritdoc />
    public void Initialize(string agentId, string conversationId, int turnNumber)
    {
        // Guard against scope leak: re-initialization with a different agent or conversation
        // within the same DI scope is always a bug. Only turn number may change (subsequent turns).
        if (_initialized && (AgentId != agentId || ConversationId != conversationId))
            throw new InvalidOperationException(
                $"AgentExecutionContext scope conflict: already bound to agent '{AgentId}' / " +
                $"conversation '{ConversationId}', cannot re-initialize with agent '{agentId}' / " +
                $"conversation '{conversationId}'.");

        AgentId = agentId;
        ConversationId = conversationId;
        TurnNumber = turnNumber;
        _initialized = true;
    }
}
