using Domain.AI.Enums;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Enums;

/// <summary>
/// Tests for <see cref="AgentDirectory"/> enum — all values and count.
/// </summary>
public sealed class AgentDirectoryTests
{
    [Theory]
    [InlineData(AgentDirectory.Skills, 0)]
    [InlineData(AgentDirectory.Manifests, 1)]
    [InlineData(AgentDirectory.Mcp, 2)]
    public void Values_HaveExpectedUnderlyingIntegers(AgentDirectory value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyThreeValues()
    {
        Enum.GetValues<AgentDirectory>().Should().HaveCount(3);
    }
}
