using System.Text.Json.Serialization;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Wire shape for <see cref="LlmClaimVerifier"/>'s structured-output call. <see cref="Status"/> is a
/// free-text field validated in code (<see cref="LlmClaimVerifier"/>) rather than a C# enum — an
/// unrecognized value must fail safe as <see cref="Domain.AI.ClaimVerification.ClaimVerificationOutcome.VerifierError"/>,
/// which a failed enum deserialization would instead surface as an unhandled parse exception rather
/// than a verdict. Mirrors <c>ObligationVerificationResponse</c>'s shape and rationale exactly.
/// </summary>
internal sealed record ClaimVerificationResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }
}
