namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for obligation-based analysis (#320): extracting obligations from an artifact and
/// dispatching one verifier per obligation. Bound from <c>AppConfig:AI:Obligations</c>.
/// </summary>
public class ObligationConfig
{
    /// <summary>
    /// Whether this host wires obligation-based analysis at all. Off by default: extraction and
    /// per-obligation verification both call an LLM, so a host that never uses this should not pay
    /// for the registrations on every cold start.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of obligations extracted from one artifact that will actually be dispatched to
    /// verifiers. Guards against a pathological extraction (or an artifact deliberately shaped to
    /// provoke one) fanning out an unbounded number of verifier calls.
    /// </summary>
    /// <value>Default: 14.</value>
    public int MaxObligations { get; set; } = 14;

    /// <summary>Maximum number of verifiers dispatched concurrently for one artifact's obligations.</summary>
    /// <value>Default: 4.</value>
    public int MaxParallelVerifiers { get; set; } = 4;

    /// <summary>
    /// Per-verifier timeout. A verifier that exceeds this is treated as
    /// <c>VerificationOutcome.VerifierError</c> (fail-safe: reports as holding), not as a hang that
    /// blocks the whole run.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan PerVerifierTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
