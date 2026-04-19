using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Unit tests for <see cref="RunConversationCommand"/>, <see cref="ConversationResult"/>,
/// <see cref="TurnSummary"/>, and <see cref="TurnProgress"/> record types.
/// Verifies default values and property behavior.
/// </summary>
public class RunConversationCommandTests
{
    private static RunConversationCommand CreateCommand() => new()
    {
        AgentName = "TestAgent",
        UserMessages = ["Hello"]
    };

    // --- RunConversationCommand defaults ---

    [Fact]
    public void Timeout_DefaultValue_IsTenMinutes()
    {
        var command = CreateCommand();

        command.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void MaxTurns_DefaultValue_IsTen()
    {
        var command = CreateCommand();

        command.MaxTurns.Should().Be(10);
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
    public void WithExpression_OverridesMaxTurns()
    {
        var command = CreateCommand() with { MaxTurns = 50 };

        command.MaxTurns.Should().Be(50);
        command.AgentName.Should().Be("TestAgent");
    }

    // --- ConversationResult defaults ---

    [Fact]
    public void ConversationResult_TotalToolInvocations_DefaultIsZero()
    {
        var result = new ConversationResult
        {
            Success = true,
            Turns = [],
            FinalResponse = "done"
        };

        result.TotalToolInvocations.Should().Be(0);
    }

    [Fact]
    public void ConversationResult_Error_DefaultIsNull()
    {
        var result = new ConversationResult
        {
            Success = true,
            Turns = [],
            FinalResponse = "done"
        };

        result.Error.Should().BeNull();
    }

    // --- TurnSummary defaults ---

    [Fact]
    public void TurnSummary_ToolsInvoked_DefaultIsEmptyList()
    {
        var summary = new TurnSummary
        {
            TurnNumber = 1,
            UserMessage = "q",
            AgentResponse = "a"
        };

        summary.ToolsInvoked.Should().BeEmpty();
    }

    // --- TurnProgress shape ---

    [Fact]
    public void TurnProgress_Response_DefaultIsNull()
    {
        var progress = new TurnProgress
        {
            TurnNumber = 1,
            AgentName = "Agent",
            Status = "executing"
        };

        progress.Response.Should().BeNull();
    }

    [Fact]
    public void TurnProgress_AllProperties_SetCorrectly()
    {
        var progress = new TurnProgress
        {
            TurnNumber = 3,
            AgentName = "ResearchAgent",
            Status = "completed",
            Response = "Found 5 files"
        };

        progress.TurnNumber.Should().Be(3);
        progress.AgentName.Should().Be("ResearchAgent");
        progress.Status.Should().Be("completed");
        progress.Response.Should().Be("Found 5 files");
    }
}
