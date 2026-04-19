using Domain.AI.Compaction;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Compaction;

/// <summary>
/// Tests for compaction-related enums: <see cref="CompactionStrategy"/>,
/// <see cref="CompactionTrigger"/>, and <see cref="MicroCompactTarget"/>.
/// </summary>
public sealed class CompactionEnumTests
{
    [Theory]
    [InlineData(CompactionStrategy.Full, 0)]
    [InlineData(CompactionStrategy.Partial, 1)]
    [InlineData(CompactionStrategy.Micro, 2)]
    public void CompactionStrategy_Values_HaveExpectedIntegers(CompactionStrategy value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void CompactionStrategy_HasExactlyThreeValues()
    {
        Enum.GetValues<CompactionStrategy>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(CompactionTrigger.AutoBudget, 0)]
    [InlineData(CompactionTrigger.Manual, 1)]
    [InlineData(CompactionTrigger.CircuitBreaker, 2)]
    public void CompactionTrigger_Values_HaveExpectedIntegers(CompactionTrigger value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void CompactionTrigger_HasExactlyThreeValues()
    {
        Enum.GetValues<CompactionTrigger>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(MicroCompactTarget.FileRead, 0)]
    [InlineData(MicroCompactTarget.ShellOutput, 1)]
    [InlineData(MicroCompactTarget.GrepResult, 2)]
    [InlineData(MicroCompactTarget.GlobResult, 3)]
    [InlineData(MicroCompactTarget.WebFetch, 4)]
    [InlineData(MicroCompactTarget.LargeToolResult, 5)]
    public void MicroCompactTarget_Values_HaveExpectedIntegers(MicroCompactTarget value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void MicroCompactTarget_HasExactlySixValues()
    {
        Enum.GetValues<MicroCompactTarget>().Should().HaveCount(6);
    }
}
