namespace Domain.AI.ClaimVerification;

/// <summary>
/// How much acting on an unverified <see cref="Claim"/> could cost if the claim turns out to be
/// wrong. Assigned by code from <see cref="ClaimConsequenceSignals"/>, never by the model that
/// asserted the claim — see that type's remarks for why.
/// </summary>
public enum ClaimConsequence
{
    /// <summary>Nothing durable depends on this claim being true. Not worth verification's cost.</summary>
    Low,

    /// <summary>A write or a gated decision depends on this claim being true. Worth verifying.</summary>
    High
}
