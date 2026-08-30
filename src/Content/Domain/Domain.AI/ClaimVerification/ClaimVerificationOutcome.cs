namespace Domain.AI.ClaimVerification;

/// <summary>
/// Why a <see cref="ClaimVerdict"/> has its <see cref="ClaimVerdict.RevisedClaim"/>. Kept distinct
/// from a plain pass/fail so telemetry and audit output never collapse "checked and true," "checked
/// and false," "cited a location that doesn't exist," "could not be checked," and "skipped as
/// low-stakes" into the same observable state — the same reasoning
/// <see cref="Domain.AI.Verification.VerificationOutcome"/> documents for obligations, extended
/// here with the one outcome that must NOT be fail-safe-silent: see
/// <see cref="ClaimVerdict.LocationNotFound"/>.
/// </summary>
public enum ClaimVerificationOutcome
{
    /// <summary>The claim was checked against its cited location and holds.</summary>
    Held,

    /// <summary>The claim was checked against its cited location and does NOT hold.</summary>
    Broken,

    /// <summary>
    /// A reader recognized the claim's location scheme but the specific location does not exist.
    /// Unlike <see cref="Unverifiable"/>, this IS a real finding — the claim named something that
    /// isn't there, which is itself informative and is never reported as fail-safe silence.
    /// </summary>
    LocationNotFound,

    /// <summary>
    /// No reader was authoritative for the claim's location scheme, or the evidence found there was
    /// insufficient to judge the claim. Fail-safe: the claim is left unchanged.
    /// </summary>
    Unverifiable,

    /// <summary>The verifier itself failed before producing a real verdict. Fail-safe: unchanged.</summary>
    VerifierError,

    /// <summary>
    /// Skipped by <c>IClaimConsequenceClassifier</c> — nothing durable turns on this claim, so no
    /// reader or verifier was ever invoked.
    /// </summary>
    NotConsequential
}
