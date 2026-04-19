using Domain.AI.Agents;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="SubagentType"/> enum — all values and count.
/// </summary>
public sealed class SubagentTypeTests
{
    [Theory]
    [InlineData(SubagentType.Explore, 0)]
    [InlineData(SubagentType.Plan, 1)]
    [InlineData(SubagentType.Verify, 2)]
    [InlineData(SubagentType.Execute, 3)]
    [InlineData(SubagentType.General, 4)]
    public void Values_HaveExpectedUnderlyingIntegers(SubagentType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyFiveValues()
    {
        Enum.GetValues<SubagentType>().Should().HaveCount(5);
    }
}
