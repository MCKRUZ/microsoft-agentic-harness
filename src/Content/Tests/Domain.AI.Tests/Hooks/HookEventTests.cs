using Domain.AI.Hooks;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Hooks;

/// <summary>
/// Tests for <see cref="HookEvent"/> enum — all values and count.
/// </summary>
public sealed class HookEventTests
{
    [Theory]
    [InlineData(HookEvent.PreToolUse, 0)]
    [InlineData(HookEvent.PostToolUse, 1)]
    [InlineData(HookEvent.SessionStart, 2)]
    [InlineData(HookEvent.SessionEnd, 3)]
    [InlineData(HookEvent.PreCompact, 4)]
    [InlineData(HookEvent.PostCompact, 5)]
    [InlineData(HookEvent.SubagentStart, 6)]
    [InlineData(HookEvent.SubagentStop, 7)]
    [InlineData(HookEvent.SkillLoaded, 8)]
    [InlineData(HookEvent.SkillUnloaded, 9)]
    [InlineData(HookEvent.ToolRegistered, 10)]
    [InlineData(HookEvent.ToolUnregistered, 11)]
    [InlineData(HookEvent.PreTurn, 12)]
    [InlineData(HookEvent.PostTurn, 13)]
    [InlineData(HookEvent.BudgetWarning, 14)]
    [InlineData(HookEvent.BudgetExceeded, 15)]
    public void Values_HaveExpectedUnderlyingIntegers(HookEvent value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void Enum_HasExactlySixteenValues()
    {
        Enum.GetValues<HookEvent>().Should().HaveCount(16);
    }
}
