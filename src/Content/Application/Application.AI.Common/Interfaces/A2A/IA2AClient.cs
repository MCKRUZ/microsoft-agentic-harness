using Domain.AI.A2A;
using Domain.Common;

namespace Application.AI.Common.Interfaces.A2A;

/// <summary>
/// Caller-side A2A surface. Sends an <see cref="A2ARequest"/> to another agent
/// (in-process or cross-process) and returns the resulting <see cref="A2AResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// The client is the harness-wide ingress point for every inter-agent call. It
/// stamps caller identity onto the envelope from
/// <c>IAgentExecutionContext.AgentIdentity</c>, generates a fresh correlation id,
/// opens the <c>a2a.client</c> span, and delegates to the configured
/// <see cref="IA2AAuthenticationProvider"/> for transport-specific auth.
/// </para>
/// <para>
/// Both in-process and cross-process transports return a normalized
/// <see cref="Result{T}"/> — never throw on remote-side failures. Cancellation
/// (cooperative <see cref="CancellationToken"/>) is the only legitimate
/// exception path.
/// </para>
/// </remarks>
public interface IA2AClient
{
    /// <summary>
    /// Dispatches an A2A request to the configured transport and returns the
    /// callee's response.
    /// </summary>
    /// <param name="request">The request envelope and payload. The envelope's
    /// <c>callerAgentId</c>, <c>callerKind</c>, and <c>correlationId</c> may be
    /// pre-stamped by the caller; if absent, the client populates them from the
    /// ambient <c>IAgentExecutionContext</c> and a fresh UUIDv4.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The callee's response, wrapped in a <see cref="Result{T}"/>.
    /// A successful HTTP-level call with a <c>success=false</c> body still
    /// returns <see cref="Result{T}.Success"/> carrying the failure response —
    /// the call itself succeeded. Transport / auth / serialization failures
    /// surface as <see cref="Result.Fail(string[])"/> with stable <c>a2a.*</c>
    /// codes.</returns>
    Task<Result<A2AResponse>> CallAsync(A2ARequest request, CancellationToken cancellationToken);
}
