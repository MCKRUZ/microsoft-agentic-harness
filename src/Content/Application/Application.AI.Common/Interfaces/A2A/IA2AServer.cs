using Domain.AI.A2A;
using Domain.Common;

namespace Application.AI.Common.Interfaces.A2A;

/// <summary>
/// Callee-side A2A surface. Dispatches an inbound <see cref="A2ARequest"/> to
/// the registered local skill handler and returns its <see cref="A2AResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single dispatch entrypoint regardless of transport. The HTTP listener for
/// cross-process calls and the in-process bridge for same-process calls both
/// terminate at <see cref="DispatchAsync"/>, so authentication, identity
/// re-establishment, OTel span emission, and skill lookup live in exactly one
/// place.
/// </para>
/// <para>
/// Server implementations MUST:
/// <list type="number">
/// <item><description>Validate the inbound envelope via the registered
/// <see cref="IA2AAuthenticationProvider"/> before executing any skill code.</description></item>
/// <item><description>Establish the calling agent's identity onto
/// <c>IAgentExecutionContext</c> for the duration of the call, then restore the
/// previous identity (if any) afterwards.</description></item>
/// <item><description>Stamp the <see cref="A2AResponse.CorrelationId"/> echo
/// from the request envelope.</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IA2AServer
{
    /// <summary>
    /// Dispatches a single inbound A2A request to the resolved local skill.
    /// </summary>
    /// <param name="request">The inbound request, with its envelope already
    /// extracted by the transport layer. Authentication has NOT yet run.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A success response with the skill's output, or a failure
    /// response with a stable <c>a2a.*</c> error code. The outer
    /// <see cref="Result{T}"/> wraps catastrophic dispatch failures (e.g. the
    /// server is shutting down).</returns>
    Task<Result<A2AResponse>> DispatchAsync(A2ARequest request, CancellationToken cancellationToken);
}
