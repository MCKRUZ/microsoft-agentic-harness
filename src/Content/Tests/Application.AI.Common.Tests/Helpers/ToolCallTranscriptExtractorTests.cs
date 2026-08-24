using Application.AI.Common.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

public sealed class ToolCallTranscriptExtractorTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

    [Fact]
    public void Extract_CallWithMatchingResult_PairsThem()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "weather" })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "sunny")]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges.Should().ContainSingle();
        exchanges[0].CallId.Should().Be("call-1");
        exchanges[0].ToolName.Should().Be("search");
        exchanges[0].ArgsJson.Should().Contain("weather");
        exchanges[0].ResultText.Should().Be("sunny");
        exchanges[0].HasResult.Should().BeTrue();
        exchanges[0].RoundOrdinal.Should().Be(0);
    }

    [Fact]
    public void Extract_CallWithNoMatchingResult_ReportsOrphaned()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search")]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges.Should().ContainSingle();
        exchanges[0].HasResult.Should().BeFalse();
        exchanges[0].ResultText.Should().BeNull();
    }

    [Fact]
    public void Extract_MultipleCalls_PreservesRoundOrdinalInAppearanceOrder()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "a"),
                new FunctionCallContent("call-2", "b"),
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", "result-a"),
                new FunctionResultContent("call-2", "result-b"),
            ]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges.Should().HaveCount(2);
        exchanges[0].CallId.Should().Be("call-1");
        exchanges[0].RoundOrdinal.Should().Be(0);
        exchanges[1].CallId.Should().Be("call-2");
        exchanges[1].RoundOrdinal.Should().Be(1);
    }

    [Fact]
    public void Extract_ResultWithException_SubstitutesGenericMessage_NeverLeaksRawExceptionText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file")]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", "Error: Function failed. Exception: /etc/shadow not found")
                {
                    Exception = new InvalidOperationException("/etc/shadow not found")
                }
            ]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges[0].ResultText.Should().Be("Error: tool call failed.");
        exchanges[0].ResultText.Should().NotContain("/etc/shadow");
    }

    [Fact]
    public void Extract_NonStringResult_SerializesRatherThanCallingToString()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search")]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", new Dictionary<string, object?> { ["forecast"] = "sunny" })
            ]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges[0].ResultText.Should().Contain("forecast").And.Contain("sunny");
        exchanges[0].ResultText.Should().NotContain("Dictionary");
    }

    [Fact]
    public void Extract_CallWithNoArguments_ArgsJsonIsNull()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "ping")]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges[0].ArgsJson.Should().BeNull();
    }

    [Fact]
    public void Extract_NoToolContent_ReturnsEmpty()
    {
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "just text") };

        ToolCallTranscriptExtractor.Extract(messages, Logger).Should().BeEmpty();
    }

    [Fact]
    public void Extract_DuplicateResultsForSameCallId_LastOneWins()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search")]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", "first"),
                new FunctionResultContent("call-1", "second"),
            ]),
        };

        var exchanges = ToolCallTranscriptExtractor.Extract(messages, Logger);

        exchanges[0].ResultText.Should().Be("second");
    }
}
