using System.Text.Json;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolParameters"/> — the one conversion from JSON arguments to the dictionary
/// <c>ITool.ExecuteAsync</c> accepts.
/// </summary>
/// <remarks>
/// The CLR types here are a contract, not an implementation detail. Tools read their arguments by type
/// — <c>FileSystemTool</c> matches <c>value is string</c> and ignores anything that is not — so a
/// change of shape does not surface as a conversion error. It surfaces as a tool that quietly reports
/// a required parameter missing, in one caller's path but not the other's.
/// </remarks>
public sealed class ToolParametersTests
{
    [Fact]
    public void A_json_object_becomes_its_properties()
    {
        var result = Parse("""{"path":"src","depth":2}""");

        result.Should().HaveCount(2);
        result["path"].Should().Be("src");
    }

    [Fact]
    public void A_string_stays_a_string()
    {
        // The type tools actually check for. A string arriving as JsonElement is the shape that makes
        // a tool report its required parameter absent while the caller can see they supplied it.
        Parse("""{"path":"src"}""")["path"].Should().BeOfType<string>();
    }

    [Fact]
    public void A_whole_number_becomes_a_long()
    {
        Parse("""{"depth":2}""")["depth"].Should().Be(2L);
    }

    [Fact]
    public void A_fractional_number_becomes_a_double()
    {
        // Not truncated to an integer: a threshold of 0.75 silently becoming 0 would change what the
        // tool does rather than fail.
        Parse("""{"threshold":0.75}""")["threshold"].Should().Be(0.75d);
    }

    [Fact]
    public void A_number_too_large_for_a_long_still_survives_as_a_double()
    {
        Parse("""{"n":1e30}""")["n"].Should().Be(1e30d);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void A_boolean_becomes_a_boolean(string json, bool expected)
    {
        Parse($$"""{"flag":{{json}}}""")["flag"].Should().Be(expected);
    }

    [Fact]
    public void An_explicit_null_is_preserved_as_a_present_key()
    {
        // Distinct from an absent key: a tool distinguishing "not supplied" from "supplied as nothing"
        // needs the key to exist.
        var result = Parse("""{"note":null}""");

        result.Should().ContainKey("note");
        result["note"].Should().BeNull();
    }

    [Fact]
    public void A_nested_object_is_preserved_as_its_raw_json()
    {
        // ITool's parameter contract is flat, so a tool expecting structure parses this itself — which
        // keeps the decision about its shape with the tool that defined it.
        var result = Parse("""{"filter":{"kind":"file"}}""");

        result["filter"].Should().BeOfType<string>().Which.Should().Contain("\"kind\"");
    }

    [Fact]
    public void An_array_is_preserved_as_its_raw_json()
    {
        Parse("""{"names":["a","b"]}""")["names"].Should().BeOfType<string>().Which.Should().Contain("\"a\"");
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case()
    {
        // Tools look their parameters up by name, and the rest of the harness matches tool and
        // operation names case-insensitively. A case-sensitive map here would refuse arguments every
        // other layer accepts.
        Parse("""{"Path":"src"}""").ContainsKey("path").Should().BeTrue();
    }

    [Fact]
    public void A_json_encoded_object_inside_a_string_is_decoded()
    {
        // Models routinely emit the parameter object as an escaped string rather than an object. Left
        // undecoded the tool receives one opaque blob instead of its arguments.
        var result = ToolParameters.FromJson(
            JsonSerializer.Deserialize<JsonElement>("\"{\\\"path\\\":\\\"src\\\"}\""));

        result["path"].Should().Be("src");
    }

    [Fact]
    public void A_string_that_is_not_json_is_preserved_rather_than_discarded()
    {
        var result = ToolParameters.FromJson(JsonSerializer.Deserialize<JsonElement>("\"just text\""));

        result[ToolParameters.RawInputKey].Should().Be("just text");
    }

    [Fact]
    public void A_string_holding_a_json_array_does_not_throw()
    {
        // Valid JSON, but not an object — enumerating it as one throws. A caller (or a model) sending
        // this must get a usable answer rather than a 500 from the conversion itself.
        var act = () => ToolParameters.FromJson(JsonSerializer.Deserialize<JsonElement>("\"[1,2]\""));

        act.Should().NotThrow();
    }

    [Fact]
    public void Null_yields_an_empty_map_rather_than_failing()
    {
        // Several operations legitimately take no parameters, so absence is not an error to report.
        ToolParameters.FromJson(null).Should().BeEmpty();
    }

    [Fact]
    public void A_json_null_yields_an_empty_map()
    {
        ToolParameters.FromJson(JsonSerializer.Deserialize<JsonElement>("null")).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_string_yields_an_empty_map()
    {
        ToolParameters.FromJson(JsonSerializer.Deserialize<JsonElement>("\"\"")).Should().BeEmpty();
    }

    [Fact]
    public void The_empty_result_cannot_be_mutated_through_a_cast()
    {
        // The no-parameters answer is a single shared instance, so its immutability is load-bearing
        // rather than cosmetic. A mutable dictionary behind IReadOnlyDictionary would let any consumer
        // that downcast its parameter map corrupt the value every subsequent call receives — including
        // calls arriving from the external HTTP surface, which now shares this path with the agent.
        var empty = ToolParameters.FromJson(null);

        var act = () => ((IDictionary<string, object?>)empty).Add("injected", "value");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Two_empty_results_are_the_same_instance()
    {
        // Documents the sharing that makes the test above matter. If this ever stops being true the
        // immutability requirement relaxes — but so does the allocation saving that motivated it.
        ToolParameters.FromJson(null).Should().BeSameAs(ToolParameters.FromJson(null));
    }

    private static IReadOnlyDictionary<string, object?> Parse(string json) =>
        ToolParameters.FromJson(JsonSerializer.Deserialize<JsonElement>(json));
}
