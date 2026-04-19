using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Unit tests for the <see cref="ExecuteAgentTurnCommand"/> record type,
/// verifying default values, computed properties, and the <see cref="AgentTurnResult"/> record.
/// </summary>
public class ExecuteAgentTurnCommandTests
{
    private static ExecuteAgentTurnCommand CreateCommand(
        string agentName = "TestAgent",
        string userMessage = "Hello") => new()
    {
        AgentName = agentName,
        UserMessage = userMessage
    };

    // --- ExecuteAgentTurnCommand defaults ---

    [Fact]
    public void Timeout_DefaultValue_IsFiveMinutes()
    {
        var command = CreateCommand();

        command.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ConversationHistory_DefaultValue_IsEmptyList()
    {
        var command = CreateCommand();

        command.ConversationHistory.Should().BeEmpty();
    }

    [Fact]
    public void SystemPromptOverride_DefaultValue_IsNull()
    {
        var command = CreateCommand();

        command.SystemPromptOverride.Should().BeNull();
    }

    [Fact]
    public void DeploymentOverride_DefaultValue_IsNull()
    {
        var command = CreateCommand();

        command.DeploymentOverride.Should().BeNull();
    }

    [Fact]
    public void Temperature_DefaultValue_IsNull()
    {
        var command = CreateCommand();

        command.Temperature.Should().BeNull();
    }

    [Fact]
    public void AgentId_ReturnsAgentName()
    {
        var command = CreateCommand(agentName: "MyAgent");

        command.AgentId.Should().Be("MyAgent");
    }

    [Fact]
    public void ConversationId_DefaultValue_IsValidGuid()
    {
        var command = CreateCommand();

        Guid.TryParse(command.ConversationId, out _).Should().BeTrue();
    }

    [Fact]
    public void ConversationId_TwoInstances_AreDifferent()
    {
        var command1 = CreateCommand();
        var command2 = CreateCommand();

        command1.ConversationId.Should().NotBe(command2.ConversationId);
    }

    [Fact]
    public void TurnNumber_DefaultValue_IsZero()
    {
        var command = CreateCommand();

        command.TurnNumber.Should().Be(0);
    }

    [Fact]
    public void WithExpression_PreservesOtherProperties()
    {
        var command = CreateCommand() with
        {
            SystemPromptOverride = "custom",
            DeploymentOverride = "gpt-4o",
            Temperature = 0.7f,
            TurnNumber = 3
        };

        command.AgentName.Should().Be("TestAgent");
        command.UserMessage.Should().Be("Hello");
        command.SystemPromptOverride.Should().Be("custom");
        command.DeploymentOverride.Should().Be("gpt-4o");
        command.Temperature.Should().Be(0.7f);
        command.TurnNumber.Should().Be(3);
    }

    // --- AgentTurnResult defaults ---

    [Fact]
    public void AgentTurnResult_ToolsInvoked_DefaultValue_IsEmptyList()
    {
        var result = new AgentTurnResult
        {
            Success = true,
            Response = "ok",
            UpdatedHistory = []
        };

        result.ToolsInvoked.Should().BeEmpty();
    }

    [Fact]
    public void AgentTurnResult_Error_DefaultValue_IsNull()
    {
        var result = new AgentTurnResult
        {
            Success = true,
            Response = "ok",
            UpdatedHistory = []
        };

        result.Error.Should().BeNull();
    }

    [Fact]
    public void AgentTurnResult_SuccessResult_HasExpectedShape()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, "a")
        };

        var result = new AgentTurnResult
        {
            Success = true,
            Response = "answer",
            UpdatedHistory = history,
            ToolsInvoked = ["tool1", "tool2"]
        };

        result.Success.Should().BeTrue();
        result.Response.Should().Be("answer");
        result.UpdatedHistory.Should().HaveCount(2);
        result.ToolsInvoked.Should().Equal("tool1", "tool2");
    }

    [Fact]
    public void AgentTurnResult_FailureResult_HasErrorMessage()
    {
        var result = new AgentTurnResult
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = "Something went wrong"
        };

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Something went wrong");
    }
}
