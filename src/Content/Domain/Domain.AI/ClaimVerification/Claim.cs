namespace Domain.AI.ClaimVerification;

/// <summary>
/// One falsifiable assertion an agent made about something outside its own context — "the handler
/// at <c>Foo.cs:42</c> never disposes the connection," "the current value of
/// <c>RetryConfig.MaxAttempts</c> is 3" — that can be checked against the thing it is about rather
/// than trusted on the agent's own confidence.
/// </summary>
/// <remarks>
/// Deliberately not a reuse of <see cref="Domain.AI.Verification.Obligation"/>: an obligation names
/// two locations already known to sit inside the SAME already-fetched artifact text, while a claim's
/// evidence has to be independently resolved (and may not exist at all) via <see cref="Location"/> —
/// see <c>Application.AI.Common.Interfaces.ClaimVerification.ILocatedArtifactReader</c>.
/// </remarks>
public sealed record Claim
{
    /// <summary>The claim's own text — what is being asserted.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Where to check the claim: a scheme-prefixed string (e.g. <c>"file:src/Foo.cs:42"</c>,
    /// <c>"config:AI.Resilience.Retry.MaxAttempts"</c>). The substring before the first
    /// <c>':'</c> selects which registered <c>ILocatedArtifactReader</c> resolves it.
    /// </summary>
    public required string Location { get; init; }

    /// <summary>
    /// How much the claim should be trusted, in <c>[0, 1]</c>. Starts at the asserting agent's own
    /// confidence (default <c>1.0</c> when unstated) and is only ever revised downward by
    /// verification — see <see cref="ClaimVerdict"/>'s factories. A failed claim is revised, never
    /// deleted, so its history stays visible to whatever consumed it.
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Code-owned facts about what acting on this claim would do — never inferred from
    /// <see cref="Text"/>, which the asserting model controls. Drives whether verification is
    /// worth its cost; see <see cref="ClaimConsequence"/>.
    /// </summary>
    public required ClaimConsequenceSignals ConsequenceSignals { get; init; }
}
