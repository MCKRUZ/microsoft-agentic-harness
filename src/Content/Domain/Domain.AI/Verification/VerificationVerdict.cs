namespace Domain.AI.Verification;

/// <summary>
/// The result of checking one <see cref="Verification.Obligation"/> against its cited location.
/// </summary>
/// <param name="Obligation">The obligation this verdict is about.</param>
/// <param name="Holds">
/// Whether the obligation should be treated as holding. Fail-safe by construction: use the static
/// factories below rather than constructing this directly — they are the only place that decides
/// this value from <see cref="Outcome"/>, so a caller cannot accidentally pair
/// <see cref="VerificationOutcome.Unverifiable"/> or <see cref="VerificationOutcome.VerifierError"/>
/// with <see langword="false"/>.
/// </param>
/// <param name="Outcome">Why <see cref="Holds"/> has its value — see that type's remarks for why this
/// is a separate field rather than folded into <see cref="Holds"/>.</param>
/// <param name="Explanation">
/// Free-text detail: the reason it is broken (<see cref="VerificationOutcome.Broken"/>), why the
/// location could not be verified (<see cref="VerificationOutcome.Unverifiable"/>), or the verifier
/// failure (<see cref="VerificationOutcome.VerifierError"/>). <see langword="null"/> for
/// <see cref="VerificationOutcome.Held"/>, which needs none.
/// </param>
public sealed record VerificationVerdict(
    Obligation Obligation, bool Holds, VerificationOutcome Outcome, string? Explanation = null)
{
    /// <summary>The obligation was checked and holds.</summary>
    public static VerificationVerdict Held(Obligation obligation) =>
        new(obligation, Holds: true, VerificationOutcome.Held);

    /// <summary>The obligation was checked and does NOT hold — a real finding.</summary>
    public static VerificationVerdict Broken(Obligation obligation, string explanation) =>
        new(obligation, Holds: false, VerificationOutcome.Broken, explanation);

    /// <summary>No reader was authoritative for the obligation's location, or it could not be
    /// resolved. Fail-safe: reports as holding.</summary>
    public static VerificationVerdict Unverifiable(Obligation obligation, string reason) =>
        new(obligation, Holds: true, VerificationOutcome.Unverifiable, reason);

    /// <summary>The verifier itself failed before producing a real verdict. Fail-safe: reports as
    /// holding.</summary>
    public static VerificationVerdict VerifierError(Obligation obligation, string reason) =>
        new(obligation, Holds: true, VerificationOutcome.VerifierError, reason);
}
