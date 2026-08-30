using System.Text.Json.Serialization;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Wire shape for <see cref="LlmObligationVerifier"/>'s structured-output call. <see cref="Status"/>
/// is a free-text field validated in code (<see cref="LlmObligationVerifier"/>) rather than a C#
/// enum — an unrecognized value must fail safe as
/// <see cref="Domain.AI.Verification.VerificationOutcome.VerifierError"/>, which a failed enum
/// deserialization would instead surface as an unhandled parse exception rather than a verdict.
/// </summary>
internal sealed record ObligationVerificationResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }
}
