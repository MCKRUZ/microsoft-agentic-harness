using Application.AI.Common.Interfaces.Agent;
using Domain.AI.A2A;
using Domain.AI.Identity;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Bridges agent identity between the ambient
/// <see cref="IAgentExecutionContext"/> and the wire-shape
/// <see cref="A2AEnvelope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Used by both client (caller) and server (callee) sides:
/// </para>
/// <list type="bullet">
/// <item><description>Client: <see cref="StampOutboundIdentity"/> reads the
/// ambient <see cref="AgentIdentity"/> and writes its id/kind onto a fresh
/// envelope. Throws when no identity is set — outbound A2A without an
/// identity is a contract violation; fail loud at the call site.</description></item>
/// <item><description>Server: <see cref="EstablishInboundIdentity"/> wraps a
/// <see cref="IAgentExecutionContext.SetIdentity"/> call so the inbound caller
/// id becomes the ambient identity for the duration of the skill handler.
/// The previous identity (if any) is preserved by the scoped DI lifetime.</description></item>
/// </list>
/// </remarks>
public sealed class A2AIdentityPropagator
{
    private readonly IAgentExecutionContext _executionContext;

    /// <summary>Creates a new propagator.</summary>
    /// <param name="executionContext">Ambient agent execution context.</param>
    public A2AIdentityPropagator(IAgentExecutionContext executionContext)
    {
        _executionContext = executionContext;
    }

    /// <summary>
    /// Reads the ambient agent identity and returns its <c>(callerAgentId, callerKind)</c>
    /// pair for inclusion in an outbound envelope.
    /// </summary>
    /// <exception cref="InvalidOperationException">No ambient agent identity is set.</exception>
    public (string CallerAgentId, string CallerKind) StampOutboundIdentity()
    {
        var identity = _executionContext.AgentIdentity
            ?? throw new InvalidOperationException(
                "A2A call attempted with no ambient agent identity. " +
                "Calls must originate from within an agent execution scope established by AgentFactory.");

        return (identity.Id, identity.Kind.ToString());
    }

    /// <summary>
    /// Establishes the inbound caller's identity on the ambient execution
    /// context for the duration of the call.
    /// </summary>
    /// <param name="authoritativeCallerId">The caller id confirmed by the auth provider.</param>
    /// <param name="envelope">The envelope as received.</param>
    public void EstablishInboundIdentity(string authoritativeCallerId, A2AEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (string.IsNullOrEmpty(authoritativeCallerId))
            throw new ArgumentException("Caller id is required.", nameof(authoritativeCallerId));

        var kind = Enum.TryParse<AgentIdentityKind>(envelope.CallerKind, out var parsed)
            ? parsed
            : AgentIdentityKind.Unspecified;

        var identity = new AgentIdentity
        {
            Id = authoritativeCallerId,
            Kind = kind
        };

        _executionContext.SetIdentity(identity);
    }
}
