using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Tests for <see cref="BudgetAssessment"/> and <see cref="TokenBudgetAction"/> — construction, equality.
/// </summary>
public sealed class BudgetAssessmentTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var assessment = new BudgetAssessment
        {
            Action = TokenBudgetAction.Continue,
            Reason = "Budget is healthy",
            ContinuationCount = 3,
            CompletionPercentage = 0.45
        };

        assessment.Action.Should().Be(TokenBudgetAction.Continue);
        assessment.Reason.Should().Be("Budget is healthy");
        assessment.ContinuationCount.Should().Be(3);
        assessment.CompletionPercentage.Should().Be(0.45);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new BudgetAssessment
        {
            Action = TokenBudgetAction.Continue,
            Reason = "OK",
            ContinuationCount = 1,
            CompletionPercentage = 0.3
        };

        var updated = original with { Action = TokenBudgetAction.Nudge };

        updated.Action.Should().Be(TokenBudgetAction.Nudge);
        original.Action.Should().Be(TokenBudgetAction.Continue);
    }

    [Theory]
    [InlineData(TokenBudgetAction.Continue, 0)]
    [InlineData(TokenBudgetAction.Stop, 1)]
    [InlineData(TokenBudgetAction.Nudge, 2)]
    public void TokenBudgetAction_Values_HaveExpectedIntegers(TokenBudgetAction value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void TokenBudgetAction_HasExactlyThreeValues()
    {
        Enum.GetValues<TokenBudgetAction>().Should().HaveCount(3);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a1 = new BudgetAssessment
        {
            Action = TokenBudgetAction.Stop,
            Reason = "Budget exceeded",
            ContinuationCount = 10,
            CompletionPercentage = 1.0
        };
        var a2 = new BudgetAssessment
        {
            Action = TokenBudgetAction.Stop,
            Reason = "Budget exceeded",
            ContinuationCount = 10,
            CompletionPercentage = 1.0
        };

        a1.Should().Be(a2);
    }
}
