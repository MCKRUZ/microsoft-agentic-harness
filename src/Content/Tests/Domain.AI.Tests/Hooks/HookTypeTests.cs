using Domain.AI.Hooks;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Hooks;

/// <summary>
/// Tests for <see cref="HookType"/> enum — all values and count.
/// </summary>
public sealed class HookTypeTests
{
    [Theory]
    [InlineData(HookType.Command, 0)]
    [InlineData(HookType.Prompt, 1)]
    [InlineData(HookType.Middleware, 2)]
    [InlineData(HookType.Http, 3)]
    public void Values_HaveExpectedUnderlyingIntegers(HookType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlyFourValues()
    {
        Enum.GetValues<HookType>().Should().HaveCount(4);
    }
}
