using System.Text.Json;

namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// The single description of "what shape does this call's response take" — attached to the model
/// request as a schema hint via <see cref="StructuredOutputSchema"/>, and used to validate the
/// parsed reply on the way back. One object serving both directions, by construction, so the
/// schema sent to the model and the schema checked against its reply can never independently drift
/// (the failure shape recorded against <c>RegistrationBreakdownCalculator</c> — two estimates of
/// the same thing computed twice, agreeing only by luck).
/// </summary>
/// <remarks>
/// Build via <see cref="StructuredOutputSchema.Build{T}"/> — never construct a schema for
/// <see cref="ResponseType"/> any other way; see that type's remarks for why it must be the sole
/// owner of the schema-generation posture.
/// </remarks>
public sealed record StructuredOutputContract
{
    /// <summary>The CLR type a successful call deserializes into.</summary>
    public required Type ResponseType { get; init; }

    /// <summary>The schema's name, sent to the model as <c>ChatResponseFormatJson.SchemaName</c>.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Optional human-readable description of the schema, sent alongside the name.</summary>
    public string? SchemaDescription { get; init; }

    /// <summary>The generated JSON Schema for <see cref="ResponseType"/>.</summary>
    public required JsonElement Schema { get; init; }

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> used both to generate <see cref="Schema"/> and to
    /// deserialize the model's reply — the same reflection pass drives both, which is what makes
    /// the drift guard meaningful rather than tautological.
    /// </summary>
    public required JsonSerializerOptions SerializerOptions { get; init; }
}
