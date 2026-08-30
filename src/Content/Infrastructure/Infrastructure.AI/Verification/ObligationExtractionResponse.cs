using System.Text.Json.Serialization;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Wire shape for <see cref="LlmObligationExtractor"/>'s structured-output call — mapped to
/// <see cref="Domain.AI.Verification.Obligation"/> after a successful parse. Kept as a separate
/// Infrastructure-layer DTO rather than using the Domain record directly as the schema type, so
/// wire-schema concerns (<see langword="required"/> members driving what the generated JSON
/// schema advertises as mandatory) stay out of the Domain type — the same separation
/// <c>LlmPlanOutput</c> keeps from <c>PlanGraph</c>.
/// </summary>
internal sealed record ObligationExtractionResponse
{
    [JsonPropertyName("obligations")]
    public required IReadOnlyList<ExtractedObligationDto> Obligations { get; init; }
}

/// <summary>One obligation as returned by the model, before mapping to <see cref="Domain.AI.Verification.Obligation"/>.</summary>
internal sealed record ExtractedObligationDto
{
    [JsonPropertyName("where")]
    public required string Where { get; init; }

    [JsonPropertyName("reliesOn")]
    public required string ReliesOn { get; init; }

    [JsonPropertyName("property")]
    public required string Property { get; init; }
}
