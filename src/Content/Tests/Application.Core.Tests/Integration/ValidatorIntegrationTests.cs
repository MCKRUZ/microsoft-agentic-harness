using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using FluentValidation;
using Xunit;

namespace Application.Core.Tests.Integration;

/// <summary>
/// Integration tests for FluentValidation validators exercising all validation rules
/// with realistic command inputs. Tests validators directly without MediatR pipeline.
/// </summary>
public class ValidatorIntegrationTests
{
    // ── ExecuteAgentTurnCommandValidator ──

    [Fact]
    public void ExecuteAgentTurnValidator_ValidCommand_Passes()
    {
        var validator = new ExecuteAgentTurnCommandValidator();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "planner-agent",
            UserMessage = "Analyze the requirements"
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Hello", "Agent name is required.")]
    [InlineData("agent", "", "User message is required.")]
    public void ExecuteAgentTurnValidator_EmptyRequiredFields_Fails(
        string agentName, string userMessage, string expectedError)
    {
        var validator = new ExecuteAgentTurnCommandValidator();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = agentName,
            UserMessage = userMessage
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == expectedError);
    }

    [Fact]
    public void ExecuteAgentTurnValidator_MessageTooLong_Fails()
    {
        var validator = new ExecuteAgentTurnCommandValidator();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = new string('x', 100_001)
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("maximum length"));
    }

    [Fact]
    public void ExecuteAgentTurnValidator_MaxLengthMessage_Passes()
    {
        var validator = new ExecuteAgentTurnCommandValidator();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = new string('x', 100_000)
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    // ── RunConversationCommandValidator ──

    [Fact]
    public void RunConversationValidator_ValidCommand_Passes()
    {
        var validator = new RunConversationCommandValidator();
        var command = new RunConversationCommand
        {
            AgentName = "planner",
            UserMessages = ["Hello", "How are you?"],
            MaxTurns = 5
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void RunConversationValidator_EmptyAgentName_Fails()
    {
        var validator = new RunConversationCommandValidator();
        var command = new RunConversationCommand
        {
            AgentName = "",
            UserMessages = ["Hello"]
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Agent name is required.");
    }

    [Fact]
    public void RunConversationValidator_EmptyMessages_Fails()
    {
        var validator = new RunConversationCommandValidator();
        var command = new RunConversationCommand
        {
            AgentName = "agent",
            UserMessages = []
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("user message"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void RunConversationValidator_MaxTurnsBoundary_ValidatesCorrectly(int maxTurns, bool expectedValid)
    {
        var validator = new RunConversationCommandValidator();
        var command = new RunConversationCommand
        {
            AgentName = "agent",
            UserMessages = ["Hello"],
            MaxTurns = maxTurns
        };

        var result = validator.Validate(command);

        result.IsValid.Should().Be(expectedValid);
    }

    // ── RunOrchestratedTaskCommandValidator ──

    [Fact]
    public void RunOrchestratedTaskValidator_ValidCommand_Passes()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orchestrator",
            TaskDescription = "Build a web application",
            AvailableAgents = ["planner", "coder", "reviewer"],
            MaxTotalTurns = 20
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void RunOrchestratedTaskValidator_EmptyOrchestratorName_Fails()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "",
            TaskDescription = "Do something",
            AvailableAgents = ["agent-1"]
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Orchestrator name is required.");
    }

    [Fact]
    public void RunOrchestratedTaskValidator_EmptyTaskDescription_Fails()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orch",
            TaskDescription = "",
            AvailableAgents = ["agent-1"]
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Task description"));
    }

    [Fact]
    public void RunOrchestratedTaskValidator_TaskDescriptionTooLong_Fails()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orch",
            TaskDescription = new string('x', 50_001),
            AvailableAgents = ["agent-1"]
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("maximum length"));
    }

    [Fact]
    public void RunOrchestratedTaskValidator_EmptyAgentsList_Fails()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orch",
            TaskDescription = "Do it",
            AvailableAgents = []
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("available agent"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void RunOrchestratedTaskValidator_MaxTurnsBoundary_ValidatesCorrectly(int maxTurns, bool expectedValid)
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orch",
            TaskDescription = "Task",
            AvailableAgents = ["agent"],
            MaxTotalTurns = maxTurns
        };

        var result = validator.Validate(command);

        result.IsValid.Should().Be(expectedValid);
    }

    // ── Multiple validation errors ──

    [Fact]
    public void ExecuteAgentTurnValidator_AllFieldsEmpty_ReturnsMultipleErrors()
    {
        var validator = new ExecuteAgentTurnCommandValidator();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "",
            UserMessage = ""
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void RunOrchestratedTaskValidator_AllFieldsInvalid_ReturnsMultipleErrors()
    {
        var validator = new RunOrchestratedTaskCommandValidator();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "",
            TaskDescription = "",
            AvailableAgents = [],
            MaxTotalTurns = -1
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(3);
    }
}
