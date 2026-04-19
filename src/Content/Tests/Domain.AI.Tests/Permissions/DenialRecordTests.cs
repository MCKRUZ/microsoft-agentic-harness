using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

/// <summary>
/// Tests for <see cref="DenialRecord"/> record — construction, defaults, equality.
/// </summary>
public sealed class DenialRecordTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var record = new DenialRecord
        {
            ToolName = "bash",
            DenialCount = 3,
            FirstDenied = first,
            LastDenied = last
        };

        record.ToolName.Should().Be("bash");
        record.DenialCount.Should().Be(3);
        record.FirstDenied.Should().Be(first);
        record.LastDenied.Should().Be(last);
    }

    [Fact]
    public void Defaults_OperationPattern_IsNull()
    {
        var record = new DenialRecord
        {
            ToolName = "test",
            DenialCount = 1,
            FirstDenied = DateTimeOffset.UtcNow,
            LastDenied = DateTimeOffset.UtcNow
        };

        record.OperationPattern.Should().BeNull();
    }

    [Fact]
    public void OperationPattern_WhenSet_RetainsValue()
    {
        var record = new DenialRecord
        {
            ToolName = "file_system",
            OperationPattern = "write:*",
            DenialCount = 5,
            FirstDenied = DateTimeOffset.UtcNow,
            LastDenied = DateTimeOffset.UtcNow
        };

        record.OperationPattern.Should().Be("write:*");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var r1 = new DenialRecord { ToolName = "bash", DenialCount = 2, FirstDenied = ts, LastDenied = ts };
        var r2 = new DenialRecord { ToolName = "bash", DenialCount = 2, FirstDenied = ts, LastDenied = ts };

        r1.Should().Be(r2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new DenialRecord
        {
            ToolName = "bash",
            DenialCount = 1,
            FirstDenied = DateTimeOffset.UtcNow,
            LastDenied = DateTimeOffset.UtcNow
        };

        var updated = original with { DenialCount = 2 };

        updated.DenialCount.Should().Be(2);
        original.DenialCount.Should().Be(1);
    }
}
