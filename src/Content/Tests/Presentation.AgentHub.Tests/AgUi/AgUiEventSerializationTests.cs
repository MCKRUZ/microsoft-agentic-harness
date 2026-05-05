using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

public class AgUiEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RunStartedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new RunStartedEvent("thread-1", "run-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"RUN_STARTED\"");
        json.Should().Contain("\"threadId\":\"thread-1\"");
        json.Should().Contain("\"runId\":\"run-1\"");
    }

    [Fact]
    public void TextMessageContentEvent_Serializes_WithDelta()
    {
        var evt = new TextMessageContentEvent("msg-1", "Hello ");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"TEXT_MESSAGE_CONTENT\"");
        json.Should().Contain("\"messageId\":\"msg-1\"");
        json.Should().Contain("\"delta\":\"Hello \"");
    }

    [Fact]
    public void TextMessageStartEvent_Serializes_WithRole()
    {
        var evt = new TextMessageStartEvent("msg-1", "assistant");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"TEXT_MESSAGE_START\"");
        json.Should().Contain("\"role\":\"assistant\"");
    }

    [Fact]
    public void RunErrorEvent_Serializes_WithMessage()
    {
        var evt = new RunErrorEvent("Something went wrong");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"RUN_ERROR\"");
        json.Should().Contain("\"message\":\"Something went wrong\"");
    }

    [Fact]
    public void RunFinishedEvent_Serializes_MatchingRunStarted()
    {
        var evt = new RunFinishedEvent("thread-1", "run-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"RUN_FINISHED\"");
        json.Should().Contain("\"threadId\":\"thread-1\"");
        json.Should().Contain("\"runId\":\"run-1\"");
    }

    [Fact]
    public void TextMessageEndEvent_Serializes_WithMessageId()
    {
        var evt = new TextMessageEndEvent("msg-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
        json.Should().Contain("\"type\":\"TEXT_MESSAGE_END\"");
        json.Should().Contain("\"messageId\":\"msg-1\"");
    }
}
