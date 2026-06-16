using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Regression coverage for the solution-review finding that <see cref="CategoryBreakdown"/>
/// silently undercounted (<c>Get</c>/<c>Total</c>) and silently dropped tokens
/// (<c>Add</c>) for any unmapped <see cref="ContextCategory"/> via discard switch
/// arms (<c>_ => 0</c> / <c>_ => this</c>). The documented exhaustiveness guarantee
/// is now enforced at runtime: unmapped categories throw, mirroring
/// <see cref="ContextCategoryWireExtensions.ToWire"/>.
/// </summary>
public sealed class CategoryBreakdownSolutionReviewFixTests
{
    [Fact]
    public void Get_UnmappedCategory_ThrowsArgumentOutOfRange()
    {
        var breakdown = new CategoryBreakdown(1, 2, 3, 4, 5, 6);
        var bogus = (ContextCategory)999;

        var act = () => breakdown.Get(bogus);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_UnmappedCategory_ThrowsInsteadOfSilentlyDroppingTokens()
    {
        var breakdown = CategoryBreakdown.Empty;
        var bogus = (ContextCategory)999;

        var act = () => breakdown.Add(bogus, 100);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Get_EveryDefinedCategory_DoesNotThrow()
    {
        var breakdown = new CategoryBreakdown(1, 2, 3, 4, 5, 6);

        foreach (var category in Enum.GetValues<ContextCategory>())
        {
            var act = () => breakdown.Get(category);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Add_EveryDefinedCategory_DoesNotThrow()
    {
        var breakdown = CategoryBreakdown.Empty;

        foreach (var category in Enum.GetValues<ContextCategory>())
        {
            var act = () => breakdown.Add(category, 1);
            act.Should().NotThrow();
        }
    }
}
