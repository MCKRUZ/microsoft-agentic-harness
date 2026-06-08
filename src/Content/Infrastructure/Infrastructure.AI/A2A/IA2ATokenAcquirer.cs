using Domain.AI.A2A;
using Domain.Common;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Acquires a workload-identity JWT for an outbound A2A call. Implemented by the
/// consumer (e.g. via Azure.Identity DefaultAzureCredential for Entra-backed
/// agents) so the harness does not lock the consumer into a specific token
/// source.
/// </summary>
/// <remarks>
/// <para>
/// The cross-process auth provider invokes this once per outbound call. The
/// returned token MUST be a JWT whose "sub" claim names the calling agent and
/// whose "aud" claim matches the callee's configured
/// <c>A2ASurfaceConfig.ExpectedAudience</c>. Tokens that fail those checks on
/// the server side surface as <c>a2a.auth_rejected</c>.
/// </para>
/// </remarks>
public interface IA2ATokenAcquirer
{
    /// <summary>
    /// Acquires a workload identity JWT for the given outbound envelope.
    /// </summary>
    /// <param name="envelope">The envelope being sent. Used to derive the
    /// caller subject and target audience.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The bearer JWT as a string, or a stable <c>a2a.*</c> failure
    /// code on acquisition failure.</returns>
    Task<Result<string>> AcquireAsync(A2AEnvelope envelope, CancellationToken cancellationToken);
}
