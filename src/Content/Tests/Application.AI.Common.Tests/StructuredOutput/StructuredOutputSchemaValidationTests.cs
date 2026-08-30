using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.StructuredOutput;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.StructuredOutput;

/// <summary>
/// Proves <see cref="StructuredOutputSchemaValidation"/>'s drift check catches a schema/CLR
/// mismatch in <em>either</em> direction, and that the array-items check names the offending JSON
/// pointer — using a local test type so this exercises the generic checker's own correctness,
/// independent of any real DTO (those get their own drift test in the test project that can see
/// their internal type: <c>Infrastructure.AI.Tests</c> for <c>LlmPlanOutput</c>,
/// <c>Infrastructure.AI.RAG.Tests</c> for <c>CragResponse</c>).
/// </summary>
public sealed class StructuredOutputSchemaValidationTests
{
    private sealed record TestDto
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("tags")]
        public IReadOnlyList<string>? Tags { get; init; }
    }

    [Fact]
    public void FindDrift_MatchingContract_ReturnsEmpty()
    {
        var contract = StructuredOutputSchema.Build<TestDto>("test_dto");

        var drift = StructuredOutputSchemaValidation.FindDrift(contract);

        drift.Should().BeEmpty();
    }

    [Fact]
    public void FindDrift_SchemaPropertyWithNoClrMember_IsReported()
    {
        // Build a contract, then hand-corrupt its schema to add a property the type doesn't have —
        // simulating what a hand-written or drifted schema looks like from the checker's perspective.
        var real = StructuredOutputSchema.Build<TestDto>("test_dto");
        var corrupted = InjectExtraSchemaProperty(real, "phantom_field");

        var drift = StructuredOutputSchemaValidation.FindDrift(corrupted);

        drift.Should().Contain(d => d.Contains("phantom_field") && d.Contains("no matching CLR member"));
    }

    [Fact]
    public void FindDrift_ClrMemberWithNoSchemaProperty_IsReported()
    {
        // The other direction: remove a real property from the schema, leaving the CLR member
        // behind — simulating a type gaining a field without regenerating its schema.
        var real = StructuredOutputSchema.Build<TestDto>("test_dto");
        var corrupted = RemoveSchemaProperty(real, "count");

        var drift = StructuredOutputSchemaValidation.FindDrift(corrupted);

        drift.Should().Contain(d => d.Contains("Count") && d.Contains("no matching schema property"));
    }

    [Fact]
    public void FindDrift_RequiredMismatch_IsReportedBothDirections()
    {
        var real = StructuredOutputSchema.Build<TestDto>("test_dto");

        // Direction 1: schema marks something required that the CLR type doesn't.
        var overRequired = InjectRequired(real, "count");
        StructuredOutputSchemaValidation.FindDrift(overRequired).Should()
            .Contain(d => d.Contains("'count'") && d.Contains("not `required`"));

        // Direction 2: schema drops a requirement the CLR type actually has (Name is `required`).
        var underRequired = RemoveAllRequired(real);
        StructuredOutputSchemaValidation.FindDrift(underRequired).Should()
            .Contain(d => d.Contains("'name'") && d.Contains("does not mark it required"));
    }

    [Fact]
    public void FindArraysWithoutItems_ArrayMissingItems_ReturnsItsPointer()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "tags": { "type": "array" }
              }
            }
            """).RootElement;

        var offenders = StructuredOutputSchemaValidation.FindArraysWithoutItems(schema);

        offenders.Should().ContainSingle().Which.Should().Be("#/properties/tags");
    }

    [Fact]
    public void FindArraysWithoutItems_RealContractSchema_HasNoOffenders()
    {
        // Control: TestDto's real, correctly-generated schema (Tags: IReadOnlyList<string>?) must
        // NOT trip this — proves the check isn't just always finding something.
        var contract = StructuredOutputSchema.Build<TestDto>("test_dto");

        var offenders = StructuredOutputSchemaValidation.FindArraysWithoutItems(contract.Schema);

        offenders.Should().BeEmpty();
    }

    private sealed record DefaultedDto
    {
        [JsonPropertyName("required_field")]
        public required string RequiredField { get; init; }

        // Defaulted, not required — a model omitting it entirely must still parse.
        [JsonPropertyName("with_default")]
        public int WithDefault { get; init; } = 60;

        // Genuinely optional — a model correctly omitting it must still parse.
        [JsonPropertyName("optional")]
        public string? Optional { get; init; }
    }

    [Fact]
    public void Schema_OnlyMarksTheClrRequiredMemberAsRequired()
    {
        var contract = StructuredOutputSchema.Build<DefaultedDto>("defaulted_dto");

        contract.Schema.TryGetProperty("required", out var required).Should().BeTrue();
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        requiredNames.Should().Contain("required_field");
        requiredNames.Should().NotContain("with_default");
        requiredNames.Should().NotContain("optional");
    }

    [Fact]
    public void RoundTrip_OmittedDefaultedField_ParsesSuccessfully()
    {
        // The issue's stated trap: a naive reflected schema marks every field required, so a model
        // correctly omitting a defaulted field gets rejected. This proves our posture doesn't.
        var json = """{"required_field":"present"}""";

        var parsed = JsonSerializer.Deserialize<DefaultedDto>(json, StructuredOutputSchema.Build<DefaultedDto>("x").SerializerOptions);

        parsed.Should().NotBeNull();
        parsed!.RequiredField.Should().Be("present");
        parsed.WithDefault.Should().Be(60, "the CLR default fills in for the omitted field");
        parsed.Optional.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_MissingRequiredField_ThrowsJsonException()
    {
        var json = """{"with_default":10}""";
        var options = StructuredOutputSchema.Build<DefaultedDto>("x").SerializerOptions;

        var act = () => JsonSerializer.Deserialize<DefaultedDto>(json, options);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void RoundTrip_ModelVolunteersAnUnknownExtraKey_StillParsesSuccessfully()
    {
        // The issue's other stated trap: additionalProperties:false would reject a model that
        // volunteers an unprompted explanatory key alongside a valid payload.
        var json = """{"required_field":"present","confidence":0.9}""";
        var options = StructuredOutputSchema.Build<DefaultedDto>("x").SerializerOptions;

        var act = () => JsonSerializer.Deserialize<DefaultedDto>(json, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void FindArraysWithoutItems_ArrayWithItems_IsNotReported()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "tags": { "type": "array", "items": { "type": "string" } }
              }
            }
            """).RootElement;

        StructuredOutputSchemaValidation.FindArraysWithoutItems(schema).Should().BeEmpty();
    }

    // All four corruption helpers below reduce to the same shape — parse the schema to a mutable
    // dictionary, change one key, re-serialize — so they share it instead of each hand-rolling the
    // parse/rebuild dance.
    private static StructuredOutputContract WithSchema(
        StructuredOutputContract source, Action<Dictionary<string, JsonElement>> mutateTopLevel)
    {
        var mutable = ToDictionary(source.Schema);
        mutateTopLevel(mutable);
        return source with { Schema = ToJsonElement(mutable) };
    }

    private static StructuredOutputContract InjectExtraSchemaProperty(StructuredOutputContract source, string propertyName) =>
        WithSchema(source, top =>
        {
            var props = ToDictionary(top["properties"]);
            props[propertyName] = JsonDocument.Parse("""{"type":"string"}""").RootElement.Clone();
            top["properties"] = ToJsonElement(props);
        });

    private static StructuredOutputContract RemoveSchemaProperty(StructuredOutputContract source, string propertyName) =>
        WithSchema(source, top =>
        {
            var props = ToDictionary(top["properties"]);
            props.Remove(propertyName);
            top["properties"] = ToJsonElement(props);
        });

    private static StructuredOutputContract InjectRequired(StructuredOutputContract source, string propertyName) =>
        WithSchema(source, top =>
        {
            var existing = top.TryGetValue("required", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()!).ToList()
                : [];
            existing.Add(propertyName);
            top["required"] = JsonSerializer.SerializeToElement(existing);
        });

    private static StructuredOutputContract RemoveAllRequired(StructuredOutputContract source) =>
        WithSchema(source, top => top.Remove("required"));

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement element)
    {
        using var doc = JsonDocument.Parse(element.GetRawText());
        var result = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return result;
    }

    private static JsonElement ToJsonElement(Dictionary<string, JsonElement> obj) =>
        JsonSerializer.SerializeToElement(obj);
}
