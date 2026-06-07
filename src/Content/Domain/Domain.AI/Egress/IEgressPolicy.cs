using Domain.AI.Identity;

namespace Domain.AI.Egress;

/// <summary>
/// Per-skill outbound HTTP allowlist policy. Asked once per outbound
/// <see cref="HttpRequestMessage"/> by the egress delegating handler before the
/// request descends into the SSRF defense layer.
/// </summary>
/// <remarks>
/// <para>
/// The harness's policy is the OUTER ring of egress defense — it checks the
/// declared URI of the request against an identity-scoped allowlist. The INNER
/// ring (currently <c>Microsoft.Security.AntiSSRF</c>) performs connect-time IP
/// validation to defeat DNS rebinding and metadata-endpoint exfiltration. Both
/// rings must be satisfied for a request to leave the process; failing either
/// ring blocks the request.
/// </para>
/// <para>
/// The policy returns an <see cref="EgressDecision"/> rather than throwing on
/// deny. The delegating handler decides whether to throw
/// <see cref="EgressBlockedException"/> based on the audit configuration —
/// every decision is written to the audit log either way.
/// </para>
/// </remarks>
public interface IEgressPolicy
{
    /// <summary>
    /// Evaluate the supplied <paramref name="target"/> against the policy bound
    /// to <paramref name="identity"/> and return a verdict.
    /// </summary>
    /// <param name="target">The full URI of the outbound request, including scheme, host, port, and path.</param>
    /// <param name="identity">The agent identity initiating the request. Used to select the per-skill allowlist.</param>
    /// <param name="cancellationToken">Cancellation token honoured by any DNS resolution the policy performs.</param>
    /// <returns>An <see cref="EgressDecision"/> describing the verdict.</returns>
    Task<EgressDecision> AllowAsync(Uri target, AgentIdentity identity, CancellationToken cancellationToken);
}
