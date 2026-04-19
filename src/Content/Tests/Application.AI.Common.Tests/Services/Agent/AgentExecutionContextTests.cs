using Application.AI.Common.Services.Agent;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="AgentExecutionContext"/> covering initialization,
/// re-initialization rules, and scope conflict detection.
/// </summary>
public class AgentExecutionContextTests
{
    [Fact]
    public void NewContext_AllPropertiesAreNull()
    {
        var context = new AgentExecutionContext();

        context.AgentId.Should().BeNull();
        context.ConversationId.Should().BeNull();
        context.TurnNumber.Should().BeNull();
    }

    [Fact]
    public void Initialize_SetsAllProperties()
    {
        var context = new AgentExecutionContext();

        context.Initialize("planner", "conv-1", 1);

        context.AgentId.Should().Be("planner");
        context.ConversationId.Should().Be("conv-1");
        context.TurnNumber.Should().Be(1);
    }

    [Fact]
    public void Initialize_SameAgentAndConversation_UpdatesTurnNumber()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        context.Initialize("planner", "conv-1", 2);

        context.TurnNumber.Should().Be(2);
    }

    [Fact]
    public void Initialize_DifferentAgent_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("reviewer", "conv-1", 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scope conflict*")
            .WithMessage("*planner*")
            .WithMessage("*reviewer*");
    }

    [Fact]
    public void Initialize_DifferentConversation_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("planner", "conv-2", 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scope conflict*")
            .WithMessage("*conv-1*")
            .WithMessage("*conv-2*");
    }

    [Fact]
    public void Initialize_DifferentAgentAndConversation_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("reviewer", "conv-2", 1);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Initialize_MultipleTurns_TracksLatestTurn()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);
        context.Initialize("planner", "conv-1", 2);
        context.Initialize("planner", "conv-1", 5);

        context.TurnNumber.Should().Be(5);
    }
}
