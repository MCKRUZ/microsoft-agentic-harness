using Domain.Common;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Validates an inbound workload-identity JWT on the server side. Consumer
/// implements (e.g. via <c>Microsoft.IdentityModel.Tokens</c>) so the harness
/// does not pin a specific JWT library.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST validate:
/// </para>
/// <list type="bullet">
/// <item><description>signature against the configured key material;</description></item>
/// <item><description><c>iss</c> against <c>A2ASurfaceConfig.ExpectedIssuer</c>;</description></item>
/// <item><description><c>aud</c> against <c>A2ASurfaceConfig.ExpectedAudience</c>;</description></item>
/// <item><description><c>exp</c> with clock skew = <c>A2ASurfaceConfig.ClockSkewSeconds</c>;</description></item>
/// <item><description>token revocation status, if a revocation list is configured.</description></item>
/// </list>
/// <para>
/// Returns the JWT's <c>sub</c> claim — the authenticated caller agent id —
/// on success. Failures surface as stable <c>a2a.auth_rejected</c> codes; the
/// underlying exception is logged via structured logging on the server side
/// and never returned over the wire.
/// </para>
/// </remarks>
public interface IA2ATokenValidator
{
    /// <summary>
    /// Validates the bearer JWT and returns the authenticated caller agent id.
    /// </summary>
    /// <param name="jwt">The bearer JWT lifted from the <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The authenticated caller agent id (<c>sub</c> claim) on success,
    /// or <see cref="Result.Fail(string[])"/> with a stable <c>a2a.*</c> code.</returns>
    Task<Result<string>> ValidateAsync(string jwt, CancellationToken cancellationToken);
}
