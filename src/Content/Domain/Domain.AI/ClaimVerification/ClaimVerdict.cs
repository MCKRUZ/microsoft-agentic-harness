namespace Domain.AI.ClaimVerification;

/// <summary>
/// The result of checking one <see cref="ClaimVerification.Claim"/> against its cited location.
/// </summary>
/// <param name="Claim">The claim this verdict is about, as originally asserted.</param>
/// <param name="Outcome">Why <see cref="RevisedClaim"/> has the confidence it has.</param>
/// <param name="Explanation">
/// Free-text detail — <see langword="null"/> for <see cref="ClaimVerificationOutcome.Held"/> and
/// <see cref="ClaimVerificationOutcome.NotConsequential"/>, which need none.
/// </param>
/// <param name="RevisedClaim">
/// The claim to actually surface. Fail-safe by construction: use the static factories below rather
/// than constructing this directly — they are the only place that decides whether
/// <see cref="ClaimVerification.Claim.Confidence"/> is floored, so a caller cannot accidentally
/// pair a real finding with an unrevised claim or a fail-safe outcome with a floored one.
/// </param>
public sealed record ClaimVerdict(
    Claim Claim, ClaimVerificationOutcome Outcome, string? Explanation, Claim RevisedClaim)
{
    /// <summary>
    /// A failed claim is revised, not deleted — surfaced at this confidence rather than removed
    /// from whatever list it came from, so its history stays visible to any consumer.
    /// </summary>
    private const double FlooredConfidence = 0.1;

    /// <summary>The claim was checked and holds. Confidence unchanged.</summary>
    public static ClaimVerdict Held(Claim claim) =>
        new(claim, ClaimVerificationOutcome.Held, Explanation: null, RevisedClaim: claim);

    /// <summary>The claim was checked and does NOT hold — a real finding. Confidence floored.</summary>
    public static ClaimVerdict Broken(Claim claim, string explanation) =>
        new(claim, ClaimVerificationOutcome.Broken, explanation, claim with { Confidence = FlooredConfidence });

    /// <summary>
    /// A reader recognized the claim's location scheme but the location itself does not exist — a
    /// real finding, never fail-safe silence. Confidence floored, same as <see cref="Broken"/>.
    /// </summary>
    public static ClaimVerdict LocationNotFound(Claim claim, string reason) =>
        new(claim, ClaimVerificationOutcome.LocationNotFound, reason, claim with { Confidence = FlooredConfidence });

    /// <summary>
    /// No reader was authoritative for the claim's location scheme, or the evidence found was
    /// insufficient to judge the claim. Fail-safe: confidence unchanged.
    /// </summary>
    public static ClaimVerdict Unverifiable(Claim claim, string reason) =>
        new(claim, ClaimVerificationOutcome.Unverifiable, reason, RevisedClaim: claim);

    /// <summary>The verifier itself failed before producing a real verdict. Fail-safe: unchanged.</summary>
    public static ClaimVerdict VerifierError(Claim claim, string reason) =>
        new(claim, ClaimVerificationOutcome.VerifierError, reason, RevisedClaim: claim);

    /// <summary>Skipped as low-consequence — no reader or verifier was invoked. Confidence unchanged.</summary>
    public static ClaimVerdict NotConsequential(Claim claim) =>
        new(claim, ClaimVerificationOutcome.NotConsequential, Explanation: null, RevisedClaim: claim);
}
