using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Unit tests for <see cref="RunOrchestratedTaskCommand"/>, <see cref="OrchestratedTaskResult"/>,
/// <see cref="SubAgentResult"/>, and <see cref="OrchestrationProgress"/> record types.
/// Verifies default values, property behavior, and record equality.
/// </summary>
public class RunOrchestratedTaskCommandTests
{
    private static RunOrchestratedTaskCommand CreateCommand() => new()
    {
        OrchestratorName = "Orchestrator",
        TaskDescription = "Do work",
        AvailableAgents = ["AgentA"]
    };

    // --- RunOrchestratedTaskCommand defaults ---

    [Fact]
    public void Timeout_DefaultValue_IsTenMinutes()
    {
        var command = CreateCommand();

        command.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void MaxTotalTurns_DefaultValue_IsTwenty()
    {
        var command = CreateCommand();

        command.MaxTotalTurns.Should().Be(20);
    }

    [Fact]
    public void OnProgress_DefaultValue_IsNull()
    {
        var command = CreateCommand();

        command.OnProgress.Should().BeNull();
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
        var c1 = CreateCommand();
        var c2 = CreateCommand();

        c1.ConversationId.Should().NotBe(c2.ConversationId);
    }

    [Fact]
    public void WithExpression_OverridesMaxTotalTurns()
    {
        var command = CreateCommand() with { MaxTotalTurns = 100 };

        command.MaxTotalTurns.Should().Be(100);
        command.OrchestratorName.Should().Be("Orchestrator");
    }

    // --- OrchestratedTaskResult defaults ---

    [Fact]
    public void OrchestratedTaskResult_TotalTurns_DefaultIsZero()
    {
        var result = new OrchestratedTaskResult
        {
            Success = true,
            FinalSynthesis = "done",
            SubAgentResults = []
        };

        result.TotalTurns.Should().Be(0);
    }

    [Fact]
    public void OrchestratedTaskResult_TotalToolInvocations_DefaultIsZero()
    {
        var result = new OrchestratedTaskResult
        {
            Success = true,
            FinalSynthesis = "done",
            SubAgentResults = []
        };

        result.TotalToolInvocations.Should().Be(0);
    }

    [Fact]
    public void OrchestratedTaskResult_Error_DefaultIsNull()
    {
        var result = new OrchestratedTaskResult
        {
            Success = true,
            FinalSynthesis = "done",
            SubAgentResults = []
        };

        result.Error.Should().BeNull();
    }

    // --- SubAgentResult defaults ---

    [Fact]
    public void SubAgentResult_TurnsUsed_DefaultIsZero()
    {
        var result = new SubAgentResult
        {
            AgentName = "A",
            Subtask = "task",
            Result = "done",
            Success = true
        };

        result.TurnsUsed.Should().Be(0);
    }

    [Fact]
    public void SubAgentResult_ToolsInvoked_DefaultIsEmptyList()
    {
        var result = new SubAgentResult
        {
            AgentName = "A",
            Subtask = "task",
            Result = "done",
            Success = true
        };

        result.ToolsInvoked.Should().BeEmpty();
    }

    [Fact]
    public void SubAgentResult_AllProperties_SetCorrectly()
    {
        var result = new SubAgentResult
        {
            AgentName = "ResearchAgent",
            Subtask = "Find files",
            Result = "Found 5 files",
            Success = true,
            TurnsUsed = 3,
            ToolsInvoked = ["file_search", "read_file"]
        };

        result.AgentName.Should().Be("ResearchAgent");
        result.Subtask.Should().Be("Find files");
        result.Result.Should().Be("Found 5 files");
        result.Success.Should().BeTrue();
        result.TurnsUsed.Should().Be(3);
        result.ToolsInvoked.Should().Equal("file_search", "read_file");
    }

    // --- OrchestrationProgress shape ---

    [Fact]
    public void OrchestrationProgress_Detail_DefaultIsNull()
    {
        var progress = new OrchestrationProgress
        {
            Phase = "planning",
            AgentName = "Orchestrator",
            Status = "in_progress"
        };

        progress.Detail.Should().BeNull();
    }

    [Fact]
    public void OrchestrationProgress_AllProperties_SetCorrectly()
    {
        var progress = new OrchestrationProgress
        {
            Phase = "delegation",
            AgentName = "AgentA",
            Status = "completed",
            Detail = "Analyzed 10 files"
        };

        progress.Phase.Should().Be("delegation");
        progress.AgentName.Should().Be("AgentA");
        progress.Status.Should().Be("completed");
        progress.Detail.Should().Be("Analyzed 10 files");
    }
}
