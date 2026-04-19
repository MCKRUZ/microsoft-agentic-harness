using Domain.AI.Config;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Config;

/// <summary>
/// Tests for <see cref="ConfigScope"/> enum — values and priority ordering.
/// </summary>
public sealed class ConfigScopeTests
{
    [Theory]
    [InlineData(ConfigScope.Managed, 0)]
    [InlineData(ConfigScope.User, 1)]
    [InlineData(ConfigScope.Project, 2)]
    [InlineData(ConfigScope.Local, 3)]
    public void Values_HaveExpectedUnderlyingIntegers(ConfigScope value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyFourValues()
    {
        Enum.GetValues<ConfigScope>().Should().HaveCount(4);
    }

    [Fact]
    public void PriorityOrder_LocalIsHighestPriority()
    {
        ((int)ConfigScope.Local).Should().BeGreaterThan((int)ConfigScope.Project);
        ((int)ConfigScope.Project).Should().BeGreaterThan((int)ConfigScope.User);
        ((int)ConfigScope.User).Should().BeGreaterThan((int)ConfigScope.Managed);
    }
}
