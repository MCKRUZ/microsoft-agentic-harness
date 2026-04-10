using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Services.Agent;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public class AgentContextPropagationBehaviorTests
{
    private readonly Mock<IAgentExecutionContext> _executionContext;

    public AgentContextPropagationBehaviorTests()
    {
        _executionContext = new Mock<IAgentExecutionContext>();
    }

    [Fact]
    public async Task Handle_NonAgentScopedRequest_PassesThrough()
    {
        var behavior = CreateBehavior<NonAgentRequest, string>();
        var expected = "result";

        var result = await behavior.Handle(
            new NonAgentRequest(),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
        _executionContext.Verify(
            c => c.Initialize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AgentScopedRequest_InitializesContext()
    {
        var behavior = CreateBehavior<AgentScopedTestRequest, string>();

        await behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-42", 3),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        _executionContext.Verify(
            c => c.Initialize("planner", "conv-42", 3),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AgentScopedRequest_ReturnsHandlerResult()
    {
        var behavior = CreateBehavior<AgentScopedTestRequest, string>();
        var expected = "handler output";

        var result = await behavior.Handle(
            new AgentScopedTestRequest("agent-1", "conv-1", 1),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_AgentScopedRequest_HandlerThrows_StillPropagatesException()
    {
        var behavior = CreateBehavior<AgentScopedTestRequest, string>();

        var act = async () => await behavior.Handle(
            new AgentScopedTestRequest("agent-1", "conv-1", 1),
            () => throw new InvalidOperationException("Handler failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _executionContext.Verify(
            c => c.Initialize("agent-1", "conv-1", 1),
            Times.Once);
    }

    private AgentContextPropagationBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new AgentContextPropagationBehavior<TRequest, TResponse>(
            _executionContext.Object,
            NullLogger<AgentContextPropagationBehavior<TRequest, TResponse>>.Instance);
    }

    [Fact]
    public async Task Handle_NestedAgentScopedRequests_SameConversation_AllowsReInitialize()
    {
        // Arrange — real AgentExecutionContext: same agent + same conversation
        // should allow re-init (updates turn number for multi-turn conversations).
        var realContext = new AgentExecutionContext();
        var outerBehavior = new AgentContextPropagationBehavior<AgentScopedTestRequest, string>(
            realContext,
            NullLogger<AgentContextPropagationBehavior<AgentScopedTestRequest, string>>.Instance);

        // Act — outer initializes at turn 0, inner re-initializes at turn 1 (same agent/conv)
        var result = await outerBehavior.Handle(
            new AgentScopedTestRequest("agent-1", "conv-1", 0),
            async () =>
            {
                var innerBehavior = new AgentContextPropagationBehavior<AgentScopedTestRequest, string>(
                    realContext,
                    NullLogger<AgentContextPropagationBehavior<AgentScopedTestRequest, string>>.Instance);
                return await innerBehavior.Handle(
                    new AgentScopedTestRequest("agent-1", "conv-1", 1),
                    () => Task.FromResult("inner"),
                    CancellationToken.None);
            },
            CancellationToken.None);

        // Assert — succeeds, turn number updated to the inner value
        result.Should().Be("inner");
        realContext.TurnNumber.Should().Be(1);
    }

    [Fact]
    public async Task Handle_NestedAgentScopedRequests_DifferentConversation_ThrowsScopeConflict()
    {
        // Arrange — different conversation ID in nested request = scope leak
        var realContext = new AgentExecutionContext();
        var outerBehavior = new AgentContextPropagationBehavior<AgentScopedTestRequest, string>(
            realContext,
            NullLogger<AgentContextPropagationBehavior<AgentScopedTestRequest, string>>.Instance);

        // Act — outer initializes conv-1, inner tries conv-2 → scope conflict
        var act = async () => await outerBehavior.Handle(
            new AgentScopedTestRequest("agent-1", "conv-1", 0),
            async () =>
            {
                var innerBehavior = new AgentContextPropagationBehavior<AgentScopedTestRequest, string>(
                    realContext,
                    NullLogger<AgentContextPropagationBehavior<AgentScopedTestRequest, string>>.Instance);
                return await innerBehavior.Handle(
                    new AgentScopedTestRequest("agent-1", "conv-2", 1),
                    () => Task.FromResult("inner"),
                    CancellationToken.None);
            },
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scope conflict*");
    }

    [Fact]
    public async Task Handle_OuterNonAgentScoped_InnerAgentScoped_InitializesOnlyOnce()
    {
        // Arrange — after the fix, RunConversationCommand no longer implements
        // IAgentScopedRequest. Only ExecuteAgentTurnCommand does, so Initialize
        // is called exactly once per scope.
        var realContext = new AgentExecutionContext();
        var outerBehavior = new AgentContextPropagationBehavior<NonAgentRequest, string>(
            realContext,
            NullLogger<AgentContextPropagationBehavior<NonAgentRequest, string>>.Instance);

        // Act — outer passes through (not IAgentScopedRequest), inner initializes once
        var result = await outerBehavior.Handle(
            new NonAgentRequest(),
            async () =>
            {
                var innerBehavior = new AgentContextPropagationBehavior<AgentScopedTestRequest, string>(
                    realContext,
                    NullLogger<AgentContextPropagationBehavior<AgentScopedTestRequest, string>>.Instance);
                return await innerBehavior.Handle(
                    new AgentScopedTestRequest("agent-1", "conv-1", 1),
                    () => Task.FromResult("success"),
                    CancellationToken.None);
            },
            CancellationToken.None);

        // Assert — works, and context is set from the inner (turn) command
        result.Should().Be("success");
        realContext.AgentId.Should().Be("agent-1");
        realContext.TurnNumber.Should().Be(1);
    }

    // Test request types
    public record NonAgentRequest : IRequest<string>;

    public record AgentScopedTestRequest(
        string AgentId,
        string ConversationId,
        int TurnNumber) : IRequest<string>, IAgentScopedRequest;
}
