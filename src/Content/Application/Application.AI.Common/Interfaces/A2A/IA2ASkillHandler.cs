using Domain.AI.A2A;
using Domain.Common;

namespace Application.AI.Common.Interfaces.A2A;

/// <summary>
/// Server-side skill handler registered against an agent id (and optional skill
/// name). Resolved by the A2A server when an inbound request arrives and
/// dispatched once authentication has succeeded.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are registered via keyed DI keyed on the agent id (e.g.
/// <c>"sre-agent"</c>) so the server's dispatch loop is a single keyed lookup.
/// A null <see cref="A2AEnvelope.CalleeSkill"/> on the request resolves to the
/// handler registered against the agent id alone; a non-null skill name
/// resolves to the handler keyed on <c>"{agentId}:{skillName}"</c>.
/// </para>
/// </remarks>
public interface IA2ASkillHandler
{
    /// <summary>
    /// Handles an inbound A2A request after authentication has succeeded and
    /// the calling agent's identity has been established on
    /// <c>IAgentExecutionContext</c>.
    /// </summary>
    /// <param name="request">The validated inbound request.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The skill's response. Implementations that throw cause the
    /// server to translate the exception into an <c>a2a.skill_failed</c>
    /// response and log the inner exception via structured logging — never on
    /// the wire.</returns>
    Task<Result<A2AResponse>> HandleAsync(A2ARequest request, CancellationToken cancellationToken);
}
