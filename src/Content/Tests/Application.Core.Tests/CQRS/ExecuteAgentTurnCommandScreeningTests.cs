using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// What an agent turn hands to content safety and the prompt-injection scan (#904): every piece of caller-supplied text
/// that reaches the model, not only the message.
/// </summary>
public sealed class ExecuteAgentTurnCommandScreeningTests
{
    [Fact]
    public void ContentToScreen_IncludesThePerTurnContextAndTheSystemPromptOverride()
    {
        // An AG-UI caller sets TurnContext as easily as the message, and a conversation's settings set the override;
        // screening the message alone lets harmful or injected text through in either.
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = "a harmless question",
            TurnContext = "context the caller attached",
            SystemPromptOverride = "instructions the conversation carries",
        };

        command.ContentToScreen.Should()
            .Contain("a harmless question")
            .And.Contain("context the caller attached")
            .And.Contain("instructions the conversation carries");
    }

    [Fact]
    public void ContentToScreen_WithOnlyAMessage_IsTheMessage()
    {
        new ExecuteAgentTurnCommand { AgentName = "agent", UserMessage = "just this" }
            .ContentToScreen.Should().Be("just this");
    }
}
