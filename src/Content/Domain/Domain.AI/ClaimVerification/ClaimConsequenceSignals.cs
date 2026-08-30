namespace Domain.AI.ClaimVerification;

/// <summary>
/// Structural facts about what acting on a <see cref="Claim"/> would do, supplied by the code that
/// constructs the claim — never inferred from the claim's own text.
/// </summary>
/// <remarks>
/// If a model could label its own claims' consequence, it could opt itself out of verification by
/// declaring everything low-consequence — the same incentive problem
/// <c>Application.AI.Common.Evaluation.Judges.ViolatedClauseVerifier</c>'s remarks already name for
/// a judge grading its own verdict. These two flags are answerable by the calling code with
/// certainty (it knows whether its own call site writes anything, and whether its own gate blocks
/// on this claim), which is exactly why they are the only inputs
/// <c>IClaimConsequenceClassifier</c> accepts.
/// </remarks>
public sealed record ClaimConsequenceSignals
{
    /// <summary>
    /// Whether acting on this claim (if true) would cause a write — to a file, to live
    /// configuration, to any state outside the current call.
    /// </summary>
    public required bool CausesWrite { get; init; }

    /// <summary>
    /// Whether this claim's truth is what a decision — a blocking gate, or a human accepting an
    /// audited proposal — actually turns on.
    /// </summary>
    public required bool GatesADecision { get; init; }
}
