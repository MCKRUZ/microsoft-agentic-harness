using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// The sole owner of the schema-generation posture used across every structured-output contract.
/// No other code in this codebase may call <see cref="AIJsonUtilities.CreateJsonSchema"/> directly
/// — enforced by <c>StructuredOutputSchemaChokepointTests</c>, in the shape of
/// <c>ToolCallAdmissionChokepointTests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Posture: <c>RequireAllProperties = false</c>, <c>DisallowAdditionalProperties = false</c>,
/// <c>UseNullableKeyword = false</c>, <c>MoveDefaultKeywordToDescription = true</c>.</strong> The C#
/// type is the single source of truth for what "required" means — a member is required if and only
/// if it carries <see langword="required"/> or <c>[JsonRequired]</c>; nothing else marks a schema
/// property required, and nothing marks the schema strict.
/// </para>
/// <para>
/// This deliberately does <em>not</em> reproduce OpenAI's "strict mode" semantics
/// (<c>RequireAllProperties = true</c> + <c>additionalProperties: false</c> everywhere). Three
/// reasons: (1) it is wrong for a type built mostly from defaulted members — requiring them forces
/// the model to restate a value it already knows on every call, and makes any genuinely optional
/// member mandatory, rejecting a model that correctly omits it; (2) it cannot even be expressed for
/// a type carrying a deliberately open <see cref="JsonElement"/> blob (polymorphic per-instance
/// configuration) — strict mode forbids unconstrained objects outright; (3) it is not this layer's
/// job to apply — the OpenAI adapter already performs this exact transformation itself, correctly,
/// when a caller opts in, so hand-rolling it here would be a second, worse copy of a transform that
/// ships in a package this repo already depends on, applied even to providers (Anthropic, Azure AI
/// Inference) that would silently discard it.
/// </para>
/// <para>
/// <strong>The schema is advisory, not enforcement, on every provider.</strong> Anthropic's Messages
/// API has no JSON-schema request parameter at all — <see cref="ChatOptions.ResponseFormat"/> is
/// silently dropped on that path. The OpenAI adapter's own strict-schema transform is opt-in. So
/// receive-side validation and the repair round-trip in <see cref="StructuredOutputInvoker"/> are
/// not a nicety layered on top of structured output — on this provider matrix they ARE the
/// enforcement, and the schema attached to the request is only ever a hint the model may ignore.
/// </para>
/// </remarks>
public static class StructuredOutputSchema
{
    private static readonly AIJsonSchemaCreateOptions InferenceOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            RequireAllProperties = false,
            DisallowAdditionalProperties = false,
            UseNullableKeyword = false,
            MoveDefaultKeywordToDescription = true,
        },
    };

    /// <summary>
    /// Builds the <see cref="StructuredOutputContract"/> for <typeparamref name="T"/> — the schema
    /// generated under this type's fixed posture, and the serializer options used to both generate
    /// it and later parse a reply against it.
    /// </summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="schemaName">The schema's name (sent to the model).</param>
    /// <param name="schemaDescription">Optional description, sent alongside the name.</param>
    /// <param name="serializerOptions">
    /// Options to reflect over <typeparamref name="T"/> with. Defaults to
    /// <see cref="AIJsonUtilities.DefaultOptions"/> — camelCase, matching the shape most LLM JSON
    /// output arrives in.
    /// </param>
    public static StructuredOutputContract Build<T>(
        string schemaName, string? schemaDescription = null, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        var options = serializerOptions ?? AIJsonUtilities.DefaultOptions;
        var schema = AIJsonUtilities.CreateJsonSchema(
            type: typeof(T),
            description: schemaDescription,
            hasDefaultValue: false,
            defaultValue: null,
            serializerOptions: options,
            inferenceOptions: InferenceOptions);

        return new StructuredOutputContract
        {
            ResponseType = typeof(T),
            SchemaName = schemaName,
            SchemaDescription = schemaDescription,
            Schema = schema,
            SerializerOptions = options,
        };
    }
}
