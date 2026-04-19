using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Hooks;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

/// <summary>
/// Tests for <see cref="HookBehavior{TRequest,TResponse}"/> covering the
/// agent turn (IAgentScopedRequest) path with PreTurn/PostTurn hooks
/// and the ModifiedInput logging path.
/// </summary>
public class HookBehaviorAgentTurnTests
{
    private readonly Mock<IHookExecutor> _hookExecutor;
    private readonly Mock<IAgentExecutionContext> _executionContext;
    private readonly HookBehavior<TestAgentTurnRequest, Result<string>> _behavior;
    private readonly HookBehavior<TestToolRequest, Result<string>> _toolBehavior;

    public HookBehaviorAgentTurnTests()
    {
        _hookExecutor = new Mock<IHookExecutor>();
        _executionContext = new Mock<IAgentExecutionContext>();
        _executionContext.Setup(c => c.AgentId).Returns("test-agent");
        _executionContext.Setup(c => c.ConversationId).Returns("conv-1");
        _executionContext.Setup(c => c.TurnNumber).Returns(3);

        _behavior = new HookBehavior<TestAgentTurnRequest, Result<string>>(
            _hookExecutor.Object,
            _executionContext.Object,
            Mock.Of<ILogger<HookBehavior<TestAgentTurnRequest, Result<string>>>>());

        _toolBehavior = new HookBehavior<TestToolRequest, Result<string>>(
            _hookExecutor.Object,
            _executionContext.Object,
            Mock.Of<ILogger<HookBehavior<TestToolRequest, Result<string>>>>());
    }

    [Fact]
    public async Task AgentScopedRequest_FiresPreAndPostTurnHooks()
    {
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                It.IsAny<HookEvent>(),
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult> { HookResult.PassThrough() });

        var request = new TestAgentTurnRequest("test-agent", "conv-1", 3);
        var expectedResult = Result<string>.Success("turn completed");

        var result = await _behavior.Handle(
            request,
            () => Task.FromResult(expectedResult),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                HookEvent.PreTurn,
                It.Is<HookExecutionContext>(c =>
                    c.AgentId == "test-agent" &&
                    c.ConversationId == "conv-1" &&
                    c.TurnNumber == 3),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                HookEvent.PostTurn,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AgentScopedRequest_ContextHasNullToolName()
    {
        HookExecutionContext? capturedContext = null;
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PreTurn,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<HookEvent, HookExecutionContext, CancellationToken>((_, ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new List<HookResult> { HookResult.PassThrough() });

        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PostTurn,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult> { HookResult.PassThrough() });

        var request = new TestAgentTurnRequest("agent", "conv", 1);
        await _behavior.Handle(
            request,
            () => Task.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        capturedContext.Should().NotBeNull();
        capturedContext!.ToolName.Should().BeNull();
    }

    [Fact]
    public async Task ToolRequest_WithModifiedInput_LogsButDoesNotApply()
    {
        var modifiedInput = new Dictionary<string, object?> { ["param1"] = "modified" };
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PreToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult>
            {
                new()
                {
                    Continue = true,
                    ModifiedInput = modifiedInput
                }
            });

        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PostToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult> { HookResult.PassThrough() });

        var request = new TestToolRequest("test_tool");
        var handlerCalled = false;

        var result = await _toolBehavior.Handle(
            request,
            () =>
            {
                handlerCalled = true;
                return Task.FromResult(Result<string>.Success("ok"));
            },
            CancellationToken.None);

        handlerCalled.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ToolRequest_BlockWithNoStopReason_UsesDefaultReason()
    {
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PreToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult>
            {
                new() { Continue = false, StopReason = null }
            });

        var request = new TestToolRequest("my_tool");

        var result = await _toolBehavior.Handle(
            request,
            () => Task.FromResult(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain(e => e.Contains("my_tool"));
    }

    [Fact]
    public async Task ToolRequest_MultipleHookResults_FirstBlockWins()
    {
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PreToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult>
            {
                HookResult.PassThrough(),
                HookResult.Block("second hook blocked"),
                HookResult.Block("third hook also blocked")
            });

        var request = new TestToolRequest("test_tool");

        var result = await _toolBehavior.Handle(
            request,
            () => Task.FromResult(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("second hook blocked");
    }

    public record TestAgentTurnRequest(
        string AgentId,
        string ConversationId,
        int TurnNumber)
        : IAgentScopedRequest, IRequest<Result<string>>;

    public record TestToolRequest(string ToolName) : IToolRequest, IRequest<Result<string>>;
}
