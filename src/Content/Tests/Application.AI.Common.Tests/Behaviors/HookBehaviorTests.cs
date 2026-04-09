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

public class HookBehaviorTests
{
    private readonly Mock<IHookExecutor> _hookExecutor;
    private readonly Mock<IAgentExecutionContext> _executionContext;
    private readonly HookBehavior<TestToolRequest, Result<string>> _toolBehavior;
    private readonly HookBehavior<TestNonToolRequest, Result<string>> _nonToolBehavior;

    public HookBehaviorTests()
    {
        _hookExecutor = new Mock<IHookExecutor>();
        _executionContext = new Mock<IAgentExecutionContext>();
        _executionContext.Setup(c => c.AgentId).Returns("test-agent");
        _executionContext.Setup(c => c.ConversationId).Returns("conv-1");
        _executionContext.Setup(c => c.TurnNumber).Returns(1);

        _toolBehavior = new HookBehavior<TestToolRequest, Result<string>>(
            _hookExecutor.Object,
            _executionContext.Object,
            Mock.Of<ILogger<HookBehavior<TestToolRequest, Result<string>>>>());

        _nonToolBehavior = new HookBehavior<TestNonToolRequest, Result<string>>(
            _hookExecutor.Object,
            _executionContext.Object,
            Mock.Of<ILogger<HookBehavior<TestNonToolRequest, Result<string>>>>());
    }

    [Fact]
    public async Task ToolRequest_FiresPreAndPostToolUseHooks()
    {
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                It.IsAny<HookEvent>(),
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult> { HookResult.PassThrough() });

        var request = new TestToolRequest("file_read");
        var expectedResult = Result<string>.Success("ok");

        var result = await _toolBehavior.Handle(
            request,
            () => Task.FromResult(expectedResult),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                HookEvent.PreToolUse,
                It.Is<HookExecutionContext>(c => c.ToolName == "file_read"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                HookEvent.PostToolUse,
                It.Is<HookExecutionContext>(c => c.ToolName == "file_read"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PreToolUse_BlockingHook_ShortCircuits()
    {
        _hookExecutor
            .Setup(e => e.ExecuteHooksAsync(
                HookEvent.PreToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookResult>
            {
                HookResult.Block("Blocked by security hook")
            });

        var request = new TestToolRequest("dangerous_tool");
        var handlerCalled = false;

        var result = await _toolBehavior.Handle(
            request,
            () =>
            {
                handlerCalled = true;
                return Task.FromResult(Result<string>.Success("should not reach"));
            },
            CancellationToken.None);

        handlerCalled.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain("Blocked by security hook");

        // PostToolUse should NOT fire when blocked
        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                HookEvent.PostToolUse,
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NonToolRequest_SkipsToolHooks()
    {
        var request = new TestNonToolRequest();
        var expectedResult = Result<string>.Success("ok");

        var result = await _nonToolBehavior.Handle(
            request,
            () => Task.FromResult(expectedResult),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _hookExecutor.Verify(
            e => e.ExecuteHooksAsync(
                It.IsAny<HookEvent>(),
                It.IsAny<HookExecutionContext>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Test request types

    public record TestToolRequest(string ToolName) : IToolRequest, IRequest<Result<string>>;

    public record TestNonToolRequest : IRequest<Result<string>>;
}
