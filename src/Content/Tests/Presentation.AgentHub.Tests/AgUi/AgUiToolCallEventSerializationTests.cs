using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Verifies the three client round-trip tool-call events serialize with the AG-UI wire discriminators
/// and property names the browser expects. Serialization goes through the <see cref="AgUiEvent"/> base
/// type so the polymorphic <c>type</c> field is emitted — exactly how <c>AgUiEventWriter</c> writes them.
/// </summary>
public sealed class AgUiToolCallEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void ToolCallStartEvent_Serializes_WithIdAndName()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallStartEvent("call-1", "dashboard_control"), JsonOptions);
        json.Should().Contain("\"type\":\"TOOL_CALL_START\"");
        json.Should().Contain("\"toolCallId\":\"call-1\"");
        json.Should().Contain("\"toolCallName\":\"dashboard_control\"");
    }

    [Fact]
    public void ToolCallArgsEvent_Serializes_WithDelta()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallArgsEvent("call-1", "{\"operation\":\"navigate\"}"), JsonOptions);
        json.Should().Contain("\"type\":\"TOOL_CALL_ARGS\"");
        json.Should().Contain("\"toolCallId\":\"call-1\"");
        json.Should().Contain("\"delta\":");
        json.Should().Contain("navigate");
    }

    /// <summary>
    /// The <c>withheld</c> field is absent from the wire on a normal (non-withheld) frame — pins
    /// against a future refactor that constructs it as <c>false</c> instead of <c>null</c>, which
    /// would start serializing <c>"withheld":false</c> on every frame.
    /// </summary>
    [Fact]
    public void ToolCallArgsEvent_NotWithheld_OmitsWithheldFieldFromWire()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallArgsEvent("call-1", "{}"), JsonOptions);

        json.Should().NotContain("withheld");
    }

    /// <summary>A withheld frame carries an explicit <c>"withheld":true</c> on the wire.</summary>
    [Fact]
    public void ToolCallArgsEvent_Withheld_SerializesTheFlag()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallArgsEvent("call-1", "{}", true), JsonOptions);

        json.Should().Contain("\"withheld\":true");
    }

    [Fact]
    public void ToolCallEndEvent_Serializes_WithId()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallEndEvent("call-1"), JsonOptions);
        json.Should().Contain("\"type\":\"TOOL_CALL_END\"");
        json.Should().Contain("\"toolCallId\":\"call-1\"");
    }

    [Fact]
    public void ToolCallEvents_RoundTrip_ThroughPolymorphicBase()
    {
        var json = JsonSerializer.Serialize<AgUiEvent>(new ToolCallStartEvent("call-9", "list_metrics"), JsonOptions);

        var back = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        var typed = back.Should().BeOfType<ToolCallStartEvent>().Subject;
        typed.ToolCallId.Should().Be("call-9");
        typed.ToolCallName.Should().Be("list_metrics");
    }
}
