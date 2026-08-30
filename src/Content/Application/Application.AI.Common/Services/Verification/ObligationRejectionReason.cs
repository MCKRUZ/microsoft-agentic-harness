namespace Application.AI.Common.Services.Verification;

/// <summary>Why <see cref="ObligationValidator.Validate"/> rejected an obligation.</summary>
public enum ObligationRejectionReason
{
    /// <summary><c>Where</c> is empty or whitespace — the obligation has no anchor location.</summary>
    EmptyWhere,

    /// <summary><c>ReliesOn</c> is empty or whitespace — nothing for a verifier to locate.</summary>
    EmptyReliesOn,

    /// <summary>
    /// <c>ReliesOn</c> normalizes to the same text as <c>Where</c> — the obligation cites itself as
    /// its own dependency, which a verifier cannot check (there is nothing else to read).
    /// </summary>
    ReliesOnEqualsWhere,

    /// <summary><c>Property</c> is empty or whitespace — nothing for a verifier to check.</summary>
    EmptyProperty,
}
