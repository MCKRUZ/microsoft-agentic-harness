using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
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

    // Test request types
    public record NonAgentRequest : IRequest<string>;

    public record AgentScopedTestRequest(
        string AgentId,
        string ConversationId,
        int TurnNumber) : IRequest<string>, IAgentScopedRequest;
}
