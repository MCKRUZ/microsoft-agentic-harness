using System.Text.Json;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Pins <see cref="ToolCallRecord"/>'s forward-compat contract: <see cref="ToolCallRecord.CallId"/>
/// and <see cref="ToolCallRecord.RoundOrdinal"/> (added for #249 item 6) must deserialize cleanly
/// from a row persisted before either field existed. The Infrastructure-layer entity mapper stores
/// this record as a raw JSON blob column, so an old row's JSON genuinely lacks both properties —
/// this is not a hypothetical shape.
/// </summary>
public sealed class ToolCallRecordTests
{
    [Fact]
    public void Deserialize_OldShapeJsonWithoutCallIdOrRoundOrdinal_DefaultsBothToNull()
    {
        const string oldShapeJson = """
            {
                "ToolName": "search",
                "Input": {"query":"weather"},
                "Output": {"result":["sunny"]},
                "DurationMs": 42
            }
            """;

        var record = JsonSerializer.Deserialize<ToolCallRecord>(oldShapeJson);

        record.Should().NotBeNull();
        record!.ToolName.Should().Be("search");
        record.DurationMs.Should().Be(42);
        record.CallId.Should().BeNull();
        record.RoundOrdinal.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_NewShapeWithCallIdAndRoundOrdinal_PreservesBoth()
    {
        var original = new ToolCallRecord(
            "search",
            JsonDocument.Parse("""{"query":"weather"}""").RootElement,
            JsonDocument.Parse("""{"result":["sunny"]}""").RootElement,
            DurationMs: 42,
            CallId: "call-1",
            RoundOrdinal: 2);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ToolCallRecord>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.CallId.Should().Be("call-1");
        roundTripped.RoundOrdinal.Should().Be(2);
    }
}
