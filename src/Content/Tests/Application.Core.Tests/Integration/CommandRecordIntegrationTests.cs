using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.Core.Tests.Integration;

/// <summary>
/// Integration tests for CQRS command records and result records.
/// Verifies record immutability, default values, and record equality.
/// </summary>
public class CommandRecordIntegrationTests
{
    // ── ExecuteAgentTurnCommand ──

    [Fact]
    public void ExecuteAgentTurnCommand_DefaultValues_AreCorrect()
    {
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "test-agent",
            UserMessage = "Hello"
        };

        command.ConversationHistory.Should().BeEmpty();
        command.SystemPromptOverride.Should().BeNull();
        command.DeploymentOverride.Should().BeNull();
        command.Temperature.Should().BeNull();
        command.TurnNumber.Should().Be(0);
        command.Timeout.Should().Be(TimeSpan.FromMinutes(5));
        command.AgentId.Should().Be("test-agent");
        command.ConversationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExecuteAgentTurnCommand_WithHistory_PreservesMessages()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "First message"),
            new(ChatRole.Assistant, "First response")
        };

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = "Follow up",
            ConversationHistory = history,
            TurnNumber = 2
        };

        command.ConversationHistory.Should().HaveCount(2);
        command.TurnNumber.Should().Be(2);
    }

    [Fact]
    public void AgentTurnResult_SuccessResult_HasCorrectShape()
    {
        var result = new AgentTurnResult
        {
            Success = true,
            Response = "Agent response",
            UpdatedHistory =
            [
                new(ChatRole.User, "Hello"),
                new(ChatRole.Assistant, "Agent response")
            ],
            ToolsInvoked = ["file_read", "web_search"]
        };

        result.Success.Should().BeTrue();
        result.Response.Should().Be("Agent response");
        result.UpdatedHistory.Should().HaveCount(2);
        result.ToolsInvoked.Should().HaveCount(2);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void AgentTurnResult_FailureResult_HasError()
    {
        var result = new AgentTurnResult
        {
            Success = false,
            Response = "",
            UpdatedHistory = [],
            Error = "Model returned 404"
        };

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("404");
    }

    // ── RunConversationCommand ──

    [Fact]
    public void RunConversationCommand_DefaultValues_AreCorrect()
    {
        var command = new RunConversationCommand
        {
            AgentName = "chat-agent",
            UserMessages = ["Hello"]
        };

        command.MaxTurns.Should().Be(10);
        command.OnProgress.Should().BeNull();
        command.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        command.ConversationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConversationResult_SuccessWithTurns_HasCorrectStructure()
    {
        var result = new ConversationResult
        {
            Success = true,
            FinalResponse = "Final answer",
            Turns =
            [
                new TurnSummary
                {
                    TurnNumber = 1,
                    UserMessage = "Hello",
                    AgentResponse = "Hi there",
                    ToolsInvoked = ["search"]
                },
                new TurnSummary
                {
                    TurnNumber = 2,
                    UserMessage = "Tell me more",
                    AgentResponse = "Final answer"
                }
            ],
            TotalToolInvocations = 1
        };

        result.Success.Should().BeTrue();
        result.Turns.Should().HaveCount(2);
        result.Turns[0].ToolsInvoked.Should().ContainSingle("search");
        result.Turns[1].ToolsInvoked.Should().BeEmpty();
        result.TotalToolInvocations.Should().Be(1);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void TurnProgress_HasRequiredFields()
    {
        var progress = new TurnProgress
        {
            TurnNumber = 3,
            AgentName = "planner",
            Status = "executing",
            Response = "Working..."
        };

        progress.TurnNumber.Should().Be(3);
        progress.AgentName.Should().Be("planner");
        progress.Status.Should().Be("executing");
        progress.Response.Should().Be("Working...");
    }

    // ── RunOrchestratedTaskCommand ──

    [Fact]
    public void RunOrchestratedTaskCommand_DefaultValues_AreCorrect()
    {
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "orchestrator",
            TaskDescription = "Build a feature",
            AvailableAgents = ["planner", "coder"]
        };

        command.MaxTotalTurns.Should().Be(20);
        command.OnProgress.Should().BeNull();
        command.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        command.ConversationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void OrchestratedTaskResult_FullResult_HasCorrectStructure()
    {
        var result = new OrchestratedTaskResult
        {
            Success = true,
            FinalSynthesis = "All tasks completed successfully",
            SubAgentResults =
            [
                new SubAgentResult
                {
                    AgentName = "coder",
                    Subtask = "Implement login",
                    Result = "Login implemented",
                    Success = true,
                    TurnsUsed = 3,
                    ToolsInvoked = ["file_write", "code_analysis"]
                },
                new SubAgentResult
                {
                    AgentName = "reviewer",
                    Subtask = "Review login code",
                    Result = "LGTM",
                    Success = true,
                    TurnsUsed = 1
                }
            ],
            TotalTurns = 4,
            TotalToolInvocations = 2
        };

        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().HaveCount(2);
        result.SubAgentResults[0].ToolsInvoked.Should().HaveCount(2);
        result.SubAgentResults[1].ToolsInvoked.Should().BeEmpty();
        result.TotalTurns.Should().Be(4);
    }

    [Fact]
    public void OrchestrationProgress_AllPhases_Work()
    {
        var phases = new[] { "planning", "delegating", "executing", "synthesizing" };

        foreach (var phase in phases)
        {
            var progress = new OrchestrationProgress
            {
                Phase = phase,
                AgentName = "orchestrator",
                Status = "active",
                Detail = $"Processing {phase}"
            };

            progress.Phase.Should().Be(phase);
        }
    }

    // ── Record equality ──

    [Fact]
    public void ExecuteAgentTurnCommand_RecordEquality_WorksCorrectly()
    {
        var conversationId = Guid.NewGuid().ToString();

        var cmd1 = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = "Hello",
            ConversationId = conversationId,
            TurnNumber = 1
        };

        var cmd2 = new ExecuteAgentTurnCommand
        {
            AgentName = "agent",
            UserMessage = "Hello",
            ConversationId = conversationId,
            TurnNumber = 1
        };

        // Records have value equality
        cmd1.Should().Be(cmd2);
    }

    [Fact]
    public void SubAgentResult_DefaultToolsInvoked_IsEmpty()
    {
        var result = new SubAgentResult
        {
            AgentName = "coder",
            Subtask = "Code",
            Result = "Done",
            Success = true
        };

        result.ToolsInvoked.Should().BeEmpty();
    }
}
