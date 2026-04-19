using Application.AI.Common.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="TokenEstimationHelper"/> exercising estimation,
/// budget checking, and truncation across realistic prompt and message scenarios.
/// </summary>
public class TokenEstimationHelperIntegrationTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("Hi", 1)]
    [InlineData("Hello, world!", 4)] // 13 chars / 4 = ~4 tokens (ceil)
    public void EstimateTokens_SingleText_CorrectEstimate(string? text, int expected)
    {
        TokenEstimationHelper.EstimateTokens(text).Should().Be(expected);
    }

    [Fact]
    public void EstimateTokens_LongSystemPrompt_ReasonableEstimate()
    {
        var systemPrompt = new string('x', 4000); // 4000 chars = 1000 tokens

        TokenEstimationHelper.EstimateTokens(systemPrompt).Should().Be(1000);
    }

    [Fact]
    public void EstimateTokens_MultipleSegments_SumsCorrectly()
    {
        var segments = new[]
        {
            "System prompt with instructions",     // 31 chars -> 8 tokens
            "Tool schema definitions",              // 23 chars -> 6 tokens
            null,                                   // 0 tokens
            "User message content"                  // 20 chars -> 5 tokens
        };

        var total = TokenEstimationHelper.EstimateTokens(segments);

        total.Should().BeGreaterThan(0);
        total.Should().Be(
            TokenEstimationHelper.EstimateTokens(segments[0]) +
            TokenEstimationHelper.EstimateTokens(segments[1]) +
            TokenEstimationHelper.EstimateTokens(segments[3]));
    }

    [Fact]
    public void EstimateTokens_ChatMessages_SumsAcrossMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "What is the weather?"),
            new(ChatRole.Assistant, "The weather is sunny today.")
        };

        var estimate = TokenEstimationHelper.EstimateTokens(messages);

        estimate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateTokens_NullSegments_ThrowsArgumentNull()
    {
        var act = () => TokenEstimationHelper.EstimateTokens((IEnumerable<string>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EstimateTokens_NullMessages_ThrowsArgumentNull()
    {
        var act = () => TokenEstimationHelper.EstimateTokens((IReadOnlyList<ChatMessage>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FitsWithinBudget_UnderBudget_ReturnsTrue()
    {
        var text = new string('x', 100); // 25 tokens

        TokenEstimationHelper.FitsWithinBudget(text, 50).Should().BeTrue();
    }

    [Fact]
    public void FitsWithinBudget_OverBudget_ReturnsFalse()
    {
        var text = new string('x', 400); // 100 tokens

        TokenEstimationHelper.FitsWithinBudget(text, 50).Should().BeFalse();
    }

    [Fact]
    public void FitsWithinBudget_ExactBudget_ReturnsTrue()
    {
        var text = new string('x', 200); // 50 tokens

        TokenEstimationHelper.FitsWithinBudget(text, 50).Should().BeTrue();
    }

    [Fact]
    public void FitsWithinBudget_NullText_ReturnsTrue()
    {
        TokenEstimationHelper.FitsWithinBudget(null, 100).Should().BeTrue();
    }

    [Fact]
    public void TruncateToTokenBudget_ShortText_ReturnsUnchanged()
    {
        var text = "Short text";

        TokenEstimationHelper.TruncateToTokenBudget(text, 100).Should().Be("Short text");
    }

    [Fact]
    public void TruncateToTokenBudget_LongText_TruncatesWithSuffix()
    {
        var text = new string('x', 1000); // 250 tokens

        var result = TokenEstimationHelper.TruncateToTokenBudget(text, 50);

        result.Should().EndWith("...[truncated]");
        result.Length.Should().BeLessThan(1000);
    }

    [Fact]
    public void TruncateToTokenBudget_NullText_ReturnsEmpty()
    {
        TokenEstimationHelper.TruncateToTokenBudget(null, 100).Should().BeEmpty();
        TokenEstimationHelper.TruncateToTokenBudget("", 100).Should().BeEmpty();
    }

    [Fact]
    public void TruncateToTokenBudget_ZeroBudget_ThrowsArgumentOutOfRange()
    {
        var act = () => TokenEstimationHelper.TruncateToTokenBudget("text", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TruncateToTokenBudget_NegativeBudget_ThrowsArgumentOutOfRange()
    {
        var act = () => TokenEstimationHelper.TruncateToTokenBudget("text", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BudgetPlanningScenario_MultipleComponents_FitCheck()
    {
        var totalBudget = 128_000;
        var systemPrompt = new string('x', 8000);  // ~2000 tokens
        var toolSchemas = new string('x', 4000);    // ~1000 tokens
        var conversationHistory = new string('x', 20000); // ~5000 tokens

        var systemTokens = TokenEstimationHelper.EstimateTokens(systemPrompt);
        var toolTokens = TokenEstimationHelper.EstimateTokens(toolSchemas);
        var historyTokens = TokenEstimationHelper.EstimateTokens(conversationHistory);
        var totalUsed = systemTokens + toolTokens + historyTokens;

        totalUsed.Should().BeLessThan(totalBudget);

        var remainingBudget = totalBudget - totalUsed;
        var newUserMessage = new string('x', remainingBudget * 4); // exactly fills budget

        TokenEstimationHelper.FitsWithinBudget(newUserMessage, remainingBudget).Should().BeTrue();
    }
}
