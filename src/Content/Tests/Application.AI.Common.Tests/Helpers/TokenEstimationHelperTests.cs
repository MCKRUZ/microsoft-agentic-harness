using Application.AI.Common.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

public class TokenEstimationHelperTests
{
    [Fact]
    public void EstimateTokens_NullString_ReturnsZero()
    {
        TokenEstimationHelper.EstimateTokens((string?)null).Should().Be(0);
    }

    [Fact]
    public void EstimateTokens_EmptyString_ReturnsZero()
    {
        TokenEstimationHelper.EstimateTokens(string.Empty).Should().Be(0);
    }

    [Fact]
    public void EstimateTokens_ShortText_ReturnsEstimate()
    {
        // "Hello, world!" = 13 chars => ceil(13/4) = 4
        var result = TokenEstimationHelper.EstimateTokens("Hello, world!");

        result.Should().Be(4);
    }

    [Fact]
    public void EstimateTokens_ExactMultipleOfFour_ReturnsExactDivision()
    {
        // 8 chars => 8/4 = 2
        var result = TokenEstimationHelper.EstimateTokens("12345678");

        result.Should().Be(2);
    }

    [Fact]
    public void EstimateTokens_LongText_ReturnsProportionalEstimate()
    {
        var text = new string('a', 1000);
        var result = TokenEstimationHelper.EstimateTokens(text);

        result.Should().Be(250);
    }

    [Fact]
    public void EstimateTokens_SingleCharacter_ReturnsOne()
    {
        TokenEstimationHelper.EstimateTokens("x").Should().Be(1);
    }

    [Fact]
    public void EstimateTokens_Segments_SumsAllSegments()
    {
        var segments = new[] { "Hello", "World", null, "" };
        // "Hello" = 5 => 2, "World" = 5 => 2, null => 0, "" => 0 = 4 total
        var result = TokenEstimationHelper.EstimateTokens(segments);

        result.Should().Be(4);
    }

    [Fact]
    public void EstimateTokens_NullSegments_ThrowsArgumentNullException()
    {
        var act = () => TokenEstimationHelper.EstimateTokens((IEnumerable<string?>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EstimateTokens_ChatMessages_SumsAllMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt here"),  // 18 chars => 5
            new(ChatRole.User, "Hi")                      // 2 chars => 1
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        result.Should().Be(6);
    }

    [Fact]
    public void EstimateTokens_NullMessages_ThrowsArgumentNullException()
    {
        var act = () => TokenEstimationHelper.EstimateTokens((IReadOnlyList<ChatMessage>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FitsWithinBudget_UnderBudget_ReturnsTrue()
    {
        // "test" = 4 chars => 1 token
        TokenEstimationHelper.FitsWithinBudget("test", 5).Should().BeTrue();
    }

    [Fact]
    public void FitsWithinBudget_ExactBudget_ReturnsTrue()
    {
        TokenEstimationHelper.FitsWithinBudget("test", 1).Should().BeTrue();
    }

    [Fact]
    public void FitsWithinBudget_OverBudget_ReturnsFalse()
    {
        // "Hello, world!" = 13 chars => 4 tokens, budget = 2
        TokenEstimationHelper.FitsWithinBudget("Hello, world!", 2).Should().BeFalse();
    }

    [Fact]
    public void FitsWithinBudget_NullText_ReturnsTrue()
    {
        TokenEstimationHelper.FitsWithinBudget(null, 10).Should().BeTrue();
    }

    [Fact]
    public void TruncateToTokenBudget_TextFitsWithinBudget_ReturnsOriginal()
    {
        var text = "short";
        var result = TokenEstimationHelper.TruncateToTokenBudget(text, 10);

        result.Should().Be(text);
    }

    [Fact]
    public void TruncateToTokenBudget_TextExceedsBudget_ReturnsTruncatedWithSuffix()
    {
        var text = new string('a', 200);
        var result = TokenEstimationHelper.TruncateToTokenBudget(text, 10);

        result.Should().EndWith("...[truncated]");
        result.Length.Should().BeLessThanOrEqualTo(10 * 4);
    }

    [Fact]
    public void TruncateToTokenBudget_NullText_ReturnsEmpty()
    {
        TokenEstimationHelper.TruncateToTokenBudget(null, 10).Should().BeEmpty();
    }

    [Fact]
    public void TruncateToTokenBudget_EmptyText_ReturnsEmpty()
    {
        TokenEstimationHelper.TruncateToTokenBudget(string.Empty, 10).Should().BeEmpty();
    }

    [Fact]
    public void TruncateToTokenBudget_ZeroMaxTokens_ThrowsArgumentOutOfRangeException()
    {
        var act = () => TokenEstimationHelper.TruncateToTokenBudget("text", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TruncateToTokenBudget_NegativeMaxTokens_ThrowsArgumentOutOfRangeException()
    {
        var act = () => TokenEstimationHelper.TruncateToTokenBudget("text", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
