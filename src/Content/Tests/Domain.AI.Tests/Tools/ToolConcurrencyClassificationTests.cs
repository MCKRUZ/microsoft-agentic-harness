using Domain.AI.Tools;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="ToolConcurrencyClassification"/> enum — all values and fail-closed design.
/// </summary>
public sealed class ToolConcurrencyClassificationTests
{
    [Theory]
    [InlineData(ToolConcurrencyClassification.ReadOnly, 0)]
    [InlineData(ToolConcurrencyClassification.WriteSerial, 1)]
    [InlineData(ToolConcurrencyClassification.Unknown, 2)]
    public void Values_HaveExpectedUnderlyingIntegers(ToolConcurrencyClassification value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyThreeValues()
    {
        Enum.GetValues<ToolConcurrencyClassification>().Should().HaveCount(3);
    }
}
