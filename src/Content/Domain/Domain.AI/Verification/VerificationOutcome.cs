namespace Domain.AI.Verification;

/// <summary>
/// Why a <see cref="VerificationVerdict"/> ended up with its <see cref="VerificationVerdict.Holds"/>
/// value. Kept separate from <see cref="VerificationVerdict.Holds"/> rather than folded into it —
/// a bare bool cannot distinguish "we checked and it holds" from "we couldn't check so we're
/// reporting it as holding," and that distinction is the entire safety argument for the fail-safe
/// default: <see cref="Unverifiable"/> and <see cref="VerifierError"/> both set
/// <see cref="VerificationVerdict.Holds"/> to <see langword="true"/>, exactly like <see cref="Held"/>,
/// but must remain distinguishable from it in telemetry and audit output.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>The obligation was checked against its cited location and holds.</summary>
    Held,

    /// <summary>The obligation was checked against its cited location and does NOT hold — a real
    /// finding.</summary>
    Broken,

    /// <summary>No reader was authoritative for the obligation's location scheme, or the location
    /// could not be resolved by a reader that was. Fail-safe: reported as holding.</summary>
    Unverifiable,

    /// <summary>The verifier itself failed (exception, timeout) before producing a real verdict.
    /// Fail-safe: reported as holding.</summary>
    VerifierError,
}
