using Domain.AI.Egress;

namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// The sandbox-side gate that runs every URI a tool declares in
/// <c>SandboxExecutionRequest.EgressPrecheckTargets</c> through the active
/// per-skill <c>IEgressPolicy</c> BEFORE the process or container is spawned.
/// Produces a digest of the decisions for inclusion in the HMAC-signed
/// attestation.
/// </summary>
/// <remarks>
/// <para>
/// The preflight is the sandbox's "cannot bypass policy" enforcement seam.
/// In-process tools that respect the named <c>"egress"</c>
/// <see cref="HttpClient"/> are already gated by the
/// <c>EgressPolicyDelegatingHandler</c>; the preflight closes the gap for
/// subprocess tools by surfacing the URIs the tool intends to reach so the
/// policy can veto BEFORE the untrusted code runs. A tool that visits
/// destinations it did not declare here is treated as a policy violation by
/// the runtime egress audit — the audit captures actual decisions; preflight
/// pre-stamps the ones the sandbox blessed up front.
/// </para>
/// <para>
/// Implementations resolve the active <c>IEgressPolicy</c> from the ambient
/// agent identity (set by <c>AgentFactory</c>) and the
/// <c>IEgressPolicyResolver</c>. When no identity is bound — background work
/// outside an agent turn — every decision is deny by default; consumers that
/// need to bypass the gate must establish a system agent identity explicitly.
/// </para>
/// </remarks>
public interface ISandboxEgressPreflight
{
    /// <summary>
    /// Evaluate every URI in <paramref name="targets"/> against the active
    /// per-skill egress policy. Returns the decisions in declaration order so
    /// the digest is deterministic. Each decision is written to the egress
    /// audit log; the executor decides whether a deny aborts the sandbox or
    /// produces a signed failure attestation.
    /// </summary>
    /// <param name="targets">The URIs the tool intends to reach. Empty list returns an empty decision list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One <see cref="EgressDecision"/> per input URI, in input order.</returns>
    Task<IReadOnlyList<EgressDecision>> EvaluateAsync(
        IReadOnlyList<Uri> targets,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compute the stable digest fed into the HMAC attestation payload. The
    /// digest is a SHA-256 hex string over a deterministic encoding of the
    /// decision list so the verifier can reconstruct it from the JSONL egress
    /// audit and confirm the attestation matches.
    /// </summary>
    /// <param name="decisions">Decisions to digest. Empty list returns the digest of the empty string.</param>
    /// <returns>The lowercase hex SHA-256 digest. Empty list returns an empty string (sentinel for "no decisions").</returns>
    string ComputeDigest(IReadOnlyList<EgressDecision> decisions);
}
