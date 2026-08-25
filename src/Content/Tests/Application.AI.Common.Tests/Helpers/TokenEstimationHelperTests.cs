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
    public void EstimateTokens_MessageWithFunctionCall_CountsCallNameAndArguments()
    {
        // Regression guard: ChatMessage.Text concatenates only TextContent, so a message that is
        // purely a tool call previously estimated as 0 tokens — the context-budget dashboard would
        // report a conversation's largest cost category as free. Name "search" (6 chars => 2) +
        // serialized args {"query":"weather"} (19 chars => 5) = 7.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["query"] = "weather" })
            ])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        result.Should().Be(7);
    }

    [Fact]
    public void EstimateTokens_MessageWithFunctionResult_CountsResultText()
    {
        // "forecast: sunny" = 15 chars => ceil(15/4) = 4
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "forecast: sunny")])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        result.Should().Be(4);
    }

    [Fact]
    public void EstimateTokens_MessageWithFunctionCallNoArguments_CountsOnlyName()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "ping")])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        // "ping" = 4 chars => 1
        result.Should().Be(1);
    }

    [Fact]
    public void EstimateTokens_MessageWithMultipleTextContentFragments_DoesNotOvercountVersusOneConcatenatedString()
    {
        // Regression guard: a naive per-content-item estimate would ceiling-round each fragment
        // separately (e.g. "ab" => 1, "cd" => 1, summing to 2), overcounting purely because a
        // provider happened to stream one logical reply back as several TextContent blocks.
        // ChatMessage.Text concatenates them first ("abcd" = 4 chars => 1), matching what a single
        // plain-text message with the same content would estimate.
        var fragmented = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextContent("ab"), new TextContent("cd")])
        };
        var single = new List<ChatMessage> { new(ChatRole.Assistant, "abcd") };

        TokenEstimationHelper.EstimateTokens(fragmented).Should().Be(TokenEstimationHelper.EstimateTokens(single));
    }

    [Fact]
    public void EstimateTokens_MessageWithReasoningContent_CountsReasoningText()
    {
        // Regression guard: TextReasoningContent (Claude extended thinking, OpenAI o-series) is
        // real, separately-billed text ChatMessage.Text does NOT include — leaving it uncounted
        // repeats the "costliest category is free" bug for a different content type.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextReasoningContent("thinking about the weather")])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        // "thinking about the weather" = 26 chars => ceil(26/4) = 7
        result.Should().Be(7);
    }

    [Fact]
    public void EstimateTokens_MessageWithNonStringFunctionResult_SerializesRatherThanCallingToString()
    {
        // Regression guard: a structured (non-string) Result's .ToString() reflects the CLR type
        // name, not the payload — silently undercounting a large tool result returned as a raw
        // object rather than pre-serialized JSON text.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", new Dictionary<string, object?> { ["forecast"] = "sunny" })
            ])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        // Serialized {"forecast":"sunny"} = 20 chars => 20/4 = 5
        result.Should().Be(5);
    }

    [Fact]
    public void EstimateTokens_MessageWithMcpToolResult_CountsTextOutputs()
    {
        // Regression guard: every ToolResultContent subtype the SDK ships (McpServerToolResultContent,
        // WebSearchToolResultContent, CodeInterpreterToolResultContent), not just FunctionResultContent,
        // carries a text-representable Outputs payload — leaving the others at 0 would repeat the
        // "costliest category is free" bug for any consumer that wires up a hosted MCP tool.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool,
            [
                new McpServerToolResultContent("call-1")
                {
                    Outputs = [new TextContent("forecast: sunny")]
                }
            ])
        };

        var result = TokenEstimationHelper.EstimateTokens(messages);

        // "forecast: sunny" = 15 chars => ceil(15/4) = 4
        result.Should().Be(4);
    }

    [Fact]
    public void EstimateTokens_MessageWithNestedMultiFragmentToolOutputs_DoesNotOvercount()
    {
        // The concatenate-before-estimate treatment applies at every recursion depth, not just the
        // top-level message — a tool result's own Outputs list can just as plausibly be chunked into
        // several TextContent fragments as a top-level assistant reply can.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool,
            [
                new McpServerToolResultContent("call-1")
                {
                    Outputs = [new TextContent("ab"), new TextContent("cd")]
                }
            ])
        };
        var flat = new List<ChatMessage> { new(ChatRole.Tool, "abcd") };

        TokenEstimationHelper.EstimateTokens(messages).Should().Be(TokenEstimationHelper.EstimateTokens(flat));
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
