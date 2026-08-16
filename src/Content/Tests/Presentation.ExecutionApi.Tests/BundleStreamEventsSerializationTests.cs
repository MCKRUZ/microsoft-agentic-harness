using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Presentation.ExecutionApi.Streaming;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Serialization tests for the four tool-call <see cref="BundleStreamEvent"/> records — written through the
/// real <see cref="BundleStreamEventWriter"/> (matching <c>BundleRunStreamerTests</c>'s real-wire-JSON
/// approach) so a regression in the writer's serializer options is caught here too, not just the discriminator
/// wiring on <see cref="BundleStreamEvent"/> itself.
/// </summary>
public sealed class BundleStreamEventsSerializationTests
{
    /// <summary>Same shape as <c>BundleStreamEventWriter</c>'s private options, needed for the round-trip
    /// deserialize assertions (the writer only exposes serialization, not the options themselves).</summary>
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<JsonElement> WriteAndParseAsync(BundleStreamEvent evt)
    {
        using var stream = new MemoryStream();
        using (var writer = new BundleStreamEventWriter(stream))
            await writer.WriteAsync(evt, CancellationToken.None);

        var body = Encoding.UTF8.GetString(stream.ToArray());
        var json = body["data: ".Length..].TrimEnd('\n', '\r');
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task ToolCallStartEvent_SerializesWithDiscriminatorAndFields()
    {
        var element = await WriteAndParseAsync(new BundleToolCallStartEvent("call-1", "search"));

        element.GetProperty("type").GetString().Should().Be("TOOL_CALL_START");
        element.GetProperty("toolCallId").GetString().Should().Be("call-1");
        element.GetProperty("toolCallName").GetString().Should().Be("search");
    }

    [Fact]
    public async Task ToolCallArgsEvent_SerializesWithDiscriminatorAndFields()
    {
        var element = await WriteAndParseAsync(new BundleToolCallArgsEvent("call-1", "{\"q\":\"docs\"}"));

        element.GetProperty("type").GetString().Should().Be("TOOL_CALL_ARGS");
        element.GetProperty("toolCallId").GetString().Should().Be("call-1");
        element.GetProperty("delta").GetString().Should().Be("{\"q\":\"docs\"}");
    }

    /// <summary>
    /// The <c>withheld</c> field is absent from the wire on a normal (non-withheld) frame — a future
    /// refactor that starts constructing it as <c>false</c> instead of <c>null</c> would start
    /// serializing <c>"withheld":false</c> on every frame, which this pins against.
    /// </summary>
    [Fact]
    public async Task ToolCallArgsEvent_NotWithheld_OmitsWithheldFieldFromWire()
    {
        var element = await WriteAndParseAsync(new BundleToolCallArgsEvent("call-1", "{}"));

        element.TryGetProperty("withheld", out _).Should().BeFalse();
    }

    /// <summary>A withheld frame carries an explicit <c>"withheld":true</c> on the wire.</summary>
    [Fact]
    public async Task ToolCallArgsEvent_Withheld_SerializesTheFlag()
    {
        var element = await WriteAndParseAsync(new BundleToolCallArgsEvent("call-1", "{}", true));

        element.GetProperty("withheld").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallEndEvent_SerializesWithDiscriminatorAndFields()
    {
        var element = await WriteAndParseAsync(new BundleToolCallEndEvent("call-1"));

        element.GetProperty("type").GetString().Should().Be("TOOL_CALL_END");
        element.GetProperty("toolCallId").GetString().Should().Be("call-1");
    }

    [Fact]
    public async Task ToolCallResultEvent_SerializesWithDiscriminatorAndFields()
    {
        var element = await WriteAndParseAsync(new BundleToolCallResultEvent("call-1", "42 results"));

        element.GetProperty("type").GetString().Should().Be("TOOL_CALL_RESULT");
        element.GetProperty("toolCallId").GetString().Should().Be("call-1");
        element.GetProperty("result").GetString().Should().Be("42 results");
    }

    [Theory]
    [InlineData(typeof(BundleToolCallStartEvent))]
    [InlineData(typeof(BundleToolCallArgsEvent))]
    [InlineData(typeof(BundleToolCallEndEvent))]
    [InlineData(typeof(BundleToolCallResultEvent))]
    public void EachToolCallEventType_RoundTripsThroughThePolymorphicBase(Type derivedType)
    {
        BundleStreamEvent original = derivedType == typeof(BundleToolCallStartEvent)
            ? new BundleToolCallStartEvent("call-1", "search")
            : derivedType == typeof(BundleToolCallArgsEvent)
                ? new BundleToolCallArgsEvent("call-1", "{}")
                : derivedType == typeof(BundleToolCallEndEvent)
                    ? new BundleToolCallEndEvent("call-1")
                    : new BundleToolCallResultEvent("call-1", "ok");

        var json = JsonSerializer.Serialize(original, typeof(BundleStreamEvent), DeserializeOptions);
        var roundTripped = JsonSerializer.Deserialize<BundleStreamEvent>(json, DeserializeOptions);

        roundTripped.Should().BeOfType(derivedType);
        roundTripped.Should().Be(original);
    }
}
