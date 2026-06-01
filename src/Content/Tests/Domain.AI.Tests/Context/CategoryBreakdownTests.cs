using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Tests for <see cref="CategoryBreakdown"/> — total computation, Empty constant,
/// per-category get, and Add immutability.
/// </summary>
public sealed class CategoryBreakdownTests
{
    [Fact]
    public void Empty_AllSixFieldsAreZero()
    {
        var b = CategoryBreakdown.Empty;

        b.System.Should().Be(0);
        b.Agents.Should().Be(0);
        b.Skills.Should().Be(0);
        b.Tools.Should().Be(0);
        b.Mcp.Should().Be(0);
        b.Messages.Should().Be(0);
        b.Total.Should().Be(0);
    }

    [Fact]
    public void Total_SumsAllSixCategories()
    {
        var b = new CategoryBreakdown(1, 2, 4, 8, 16, 32);

        b.Total.Should().Be(63);
    }

    [Theory]
    [InlineData(ContextCategory.System, 100)]
    [InlineData(ContextCategory.Agents, 200)]
    [InlineData(ContextCategory.Skills, 300)]
    [InlineData(ContextCategory.Tools, 400)]
    [InlineData(ContextCategory.Mcp, 500)]
    [InlineData(ContextCategory.Messages, 600)]
    public void Get_ReturnsValueForCategory(ContextCategory cat, int expected)
    {
        var b = new CategoryBreakdown(100, 200, 300, 400, 500, 600);

        b.Get(cat).Should().Be(expected);
    }

    [Fact]
    public void Add_IncrementsCategoryAndLeavesOthersAlone()
    {
        var b = CategoryBreakdown.Empty
            .Add(ContextCategory.System, 50)
            .Add(ContextCategory.Messages, 75)
            .Add(ContextCategory.System, 25);

        b.System.Should().Be(75);
        b.Messages.Should().Be(75);
        b.Agents.Should().Be(0);
        b.Total.Should().Be(150);
    }

    [Fact]
    public void Add_ReturnsNewInstanceLeavingOriginalUnchanged()
    {
        var original = CategoryBreakdown.Empty;
        var updated = original.Add(ContextCategory.Tools, 99);

        original.Tools.Should().Be(0);
        updated.Tools.Should().Be(99);
        updated.Should().NotBeSameAs(original);
    }
}
