using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// Tests for <see cref="AgentContextExtensions"/> covering IsActive, GetDisplayIdentifier,
/// ToExecutionScope, and ToTelemetryTags across both active and inactive agent contexts.
/// </summary>
public class AgentContextExtensionsTests
{
    private static Mock<IAgentExecutionContext> CreateInactiveContext()
    {
        var mock = new Mock<IAgentExecutionContext>();
        mock.Setup(c => c.AgentId).Returns((string?)null);
        mock.Setup(c => c.ConversationId).Returns((string?)null);
        mock.Setup(c => c.TurnNumber).Returns((int?)null);
        return mock;
    }

    private static Mock<IAgentExecutionContext> CreateActiveContext(
        string agentId = "planner",
        string? conversationId = "conv-1",
        int? turnNumber = 3)
    {
        var mock = new Mock<IAgentExecutionContext>();
        mock.Setup(c => c.AgentId).Returns(agentId);
        mock.Setup(c => c.ConversationId).Returns(conversationId);
        mock.Setup(c => c.TurnNumber).Returns(turnNumber);
        return mock;
    }

    [Fact]
    public void IsActive_WhenAgentIdSet_ReturnsTrue()
    {
        var context = CreateActiveContext();
        context.Object.IsActive().Should().BeTrue();
    }

    [Fact]
    public void IsActive_WhenAgentIdNull_ReturnsFalse()
    {
        var context = CreateInactiveContext();
        context.Object.IsActive().Should().BeFalse();
    }

    [Fact]
    public void GetDisplayIdentifier_WhenActive_ReturnsFormattedId()
    {
        var context = CreateActiveContext("planner", turnNumber: 3);
        context.Object.GetDisplayIdentifier().Should().Be("planner@turn-3");
    }

    [Fact]
    public void GetDisplayIdentifier_WhenInactive_ReturnsNoAgent()
    {
        var context = CreateInactiveContext();
        context.Object.GetDisplayIdentifier().Should().Be("no-agent");
    }

    [Fact]
    public void ToExecutionScope_WhenActive_ReturnsScope()
    {
        var context = CreateActiveContext("planner", "conv-1", 3);
        var scope = context.Object.ToExecutionScope();

        scope.Should().NotBeNull();
        scope!.ExecutorId.Should().Be("planner");
        scope.CorrelationId.Should().Be("conv-1");
        scope.StepNumber.Should().Be(3);
    }

    [Fact]
    public void ToExecutionScope_WhenInactive_ReturnsNull()
    {
        var context = CreateInactiveContext();
        context.Object.ToExecutionScope().Should().BeNull();
    }

    [Fact]
    public void ToTelemetryTags_WhenActive_ReturnsAllTags()
    {
        var context = CreateActiveContext("planner", "conv-1", 3);
        var tags = context.Object.ToTelemetryTags();

        tags.Should().ContainKey(AgentConventions.Name).WhoseValue.Should().Be("planner");
        tags.Should().ContainKey(AgentConventions.ConversationId).WhoseValue.Should().Be("conv-1");
        tags.Should().ContainKey(AgentConventions.TurnIndex).WhoseValue.Should().Be(3);
    }

    [Fact]
    public void ToTelemetryTags_WhenActive_NoConversationId_OmitsConversationTag()
    {
        var context = CreateActiveContext("planner", conversationId: null, turnNumber: 1);
        var tags = context.Object.ToTelemetryTags();

        tags.Should().ContainKey(AgentConventions.Name);
        tags.Should().NotContainKey(AgentConventions.ConversationId);
    }

    [Fact]
    public void ToTelemetryTags_WhenActive_NoTurnNumber_OmitsTurnTag()
    {
        var context = CreateActiveContext("planner", "conv-1", turnNumber: null);
        var tags = context.Object.ToTelemetryTags();

        tags.Should().ContainKey(AgentConventions.Name);
        tags.Should().NotContainKey(AgentConventions.TurnIndex);
    }

    [Fact]
    public void ToTelemetryTags_WhenInactive_ReturnsEmptyDictionary()
    {
        var context = CreateInactiveContext();
        var tags = context.Object.ToTelemetryTags();

        tags.Should().BeEmpty();
    }

    [Fact]
    public void ToTelemetryTags_WhenInactive_ReturnsSameEmptyInstance()
    {
        var context = CreateInactiveContext();
        var first = context.Object.ToTelemetryTags();
        var second = context.Object.ToTelemetryTags();

        first.Should().BeSameAs(second);
    }
}
