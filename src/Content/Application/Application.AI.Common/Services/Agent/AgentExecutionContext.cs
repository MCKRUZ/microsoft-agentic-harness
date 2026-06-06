using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Identity;

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
    // Single gate for both Initialize and SetIdentity so the interface's
    // documented thread-safety contract holds: "multiple concurrent agent
    // requests may execute within overlapping async contexts." Without
    // locking, check-then-set on _initialized / AgentIdentity is a TOCTOU
    // window in which two writers with different values both pass the check
    // and the last writer silently wins.
    private readonly object _gate = new();
    private bool _initialized;

    /// <inheritdoc />
    public string? AgentId { get; private set; }

    /// <inheritdoc />
    public string? ConversationId { get; private set; }

    /// <inheritdoc />
    public int? TurnNumber { get; private set; }

    /// <inheritdoc />
    public AgentIdentity? AgentIdentity { get; private set; }

    /// <inheritdoc />
    public void Initialize(string agentId, string conversationId, int turnNumber)
    {
        lock (_gate)
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

    /// <inheritdoc />
    public void SetIdentity(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            // Same scope-leak guard as Initialize, applied to identity. Re-setting a
            // value-equal identity short-circuits to a literal no-op so the documented
            // idempotent contract holds without a redundant assignment in the setter.
            if (AgentIdentity is not null)
            {
                if (AgentIdentity.Equals(identity))
                    return;

                throw new InvalidOperationException(
                    $"AgentExecutionContext identity conflict: already bound to identity " +
                    $"'{AgentIdentity.Id}' (kind {AgentIdentity.Kind}), cannot re-bind to " +
                    $"identity '{identity.Id}' (kind {identity.Kind}).");
            }

            AgentIdentity = identity;
        }
    }
}
