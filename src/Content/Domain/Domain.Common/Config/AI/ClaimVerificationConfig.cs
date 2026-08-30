namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for artifact-grounded claim verification (#319): checking a high-consequence claim
/// against the artifact it cites before it is trusted. Bound from
/// <c>AppConfig:AI:ClaimVerification</c>.
/// </summary>
public class ClaimVerificationConfig
{
    /// <summary>
    /// Whether the LLM-backed verifier actually runs. Off by default: verification calls a judge
    /// model, and a host that never uses this should not pay for that call. Read-side readers
    /// (<c>ILocatedArtifactReader</c>) and the consequence classifier are registered unconditionally
    /// regardless of this flag; only the judge call itself is gated.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum number of verifiers dispatched concurrently for one batch of claims.</summary>
    /// <value>Default: 4.</value>
    public int MaxParallelVerifiers { get; set; } = 4;

    /// <summary>
    /// Per-verifier timeout. A verifier that exceeds this is treated as
    /// <c>ClaimVerificationOutcome.VerifierError</c> (fail-safe: the claim is left unchanged), not
    /// as a hang that blocks the whole batch.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan PerVerifierTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
