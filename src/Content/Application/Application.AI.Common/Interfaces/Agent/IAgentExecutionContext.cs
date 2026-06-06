using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Agent;

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
    /// Gets the workload identity of the executing agent, or <c>null</c> when identity
    /// is disabled (<c>AppConfig.AI.Identity.Enabled</c> is false), the call is outside
    /// any agent execution, or the identity has not yet been resolved by
    /// <c>AgentFactory</c>.
    /// </summary>
    /// <remarks>
    /// Separate axis from <see cref="AgentId"/> / <see cref="ConversationId"/>. Those
    /// answer "which agent and which conversation"; <see cref="AgentIdentity"/> answers
    /// "what workload identity does that agent carry for outbound RBAC". The agent id
    /// is a harness label; the identity is the Entra-bound principal.
    /// </remarks>
    AgentIdentity? AgentIdentity { get; }

    /// <summary>
    /// Initializes or updates the execution context with agent identity.
    /// Re-initialization is allowed for subsequent turns within the same agent/conversation
    /// (updates turn number). Throws if called with a different agent or conversation,
    /// which indicates a scope leak.
    /// </summary>
    void Initialize(string agentId, string conversationId, int turnNumber);

    /// <summary>
    /// Stamps the agent's workload identity onto the execution context. Called once
    /// per agent instance during agent construction (by <c>AgentFactory</c>) after the
    /// <see cref="IAgentIdentityResolver"/> resolves the identity from the credential
    /// hierarchy.
    /// </summary>
    /// <remarks>
    /// Re-set with a value-equal identity is idempotent (no throw, no state change).
    /// Re-set with a <em>different</em> identity throws
    /// <see cref="InvalidOperationException"/> — the scope is leaking across agent
    /// boundaries and the call site is wrong.
    /// </remarks>
    /// <param name="identity">The resolved agent identity. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Identity is null.</exception>
    /// <exception cref="InvalidOperationException">An identity was already set and the
    /// new one differs from it by value.</exception>
    void SetIdentity(AgentIdentity identity);
}
