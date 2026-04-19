using Domain.AI.Agents;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="AgentMessageType"/> enum — all values and underlying integers.
/// </summary>
public sealed class AgentMessageTypeTests
{
    [Theory]
    [InlineData(AgentMessageType.Task, 0)]
    [InlineData(AgentMessageType.Result, 1)]
    [InlineData(AgentMessageType.Notification, 2)]
    [InlineData(AgentMessageType.Error, 3)]
    public void Values_HaveExpectedUnderlyingIntegers(AgentMessageType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyFourValues()
    {
        Enum.GetValues<AgentMessageType>().Should().HaveCount(4);
    }
}
