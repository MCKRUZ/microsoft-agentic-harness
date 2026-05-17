using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class LlmCallStepExecutorTests
{
    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly LlmCallStepExecutor _sut;

    public LlmCallStepExecutorTests()
    {
        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new LlmCallStepExecutor(
            _sender.Object,
            _notifier.Object,
            _context,
            NullLogger<LlmCallStepExecutor>.Instance);
    }

    private static PlanStep CreateStep(StepConfiguration config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "test-llm-step",
        Type = StepType.LlmCall,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = CreateStep(new ConditionalBranchConfig
        {
            ConditionExpression = "true",
            TrueEdgeTargetId = new PlanStepId(Guid.NewGuid()),
            FalseEdgeTargetId = new PlanStepId(Guid.NewGuid())
        });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulLlmCall_ReturnsCompleted()
    {
        var config = new LlmCallConfig { SystemPrompt = "You are helpful.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);

        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = true,
                FinalResponse = "Hello world",
                Turns = []
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal("Hello world", result.Output);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_FailedLlmCall_ReturnsFailed()
    {
        var config = new LlmCallConfig { SystemPrompt = "You are helpful.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);

        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = false,
                FinalResponse = "",
                Turns = [],
                Error = "Rate limited"
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal("Rate limited", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesUpstreamOutputsInMessages()
    {
        var config = new LlmCallConfig { SystemPrompt = "Summarize.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var upstreamOutputs = new Dictionary<PlanStepId, string> { [upstreamId] = "upstream data" };

        RunConversationCommand? captured = null;
        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ConversationResult>, CancellationToken>((cmd, _) => captured = (RunConversationCommand)cmd)
            .ReturnsAsync(new ConversationResult { Success = true, FinalResponse = "done", Turns = [] });

        await _sut.ExecuteAsync(step, upstreamOutputs, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Summarize.", captured!.SystemPrompt);
        Assert.Contains("upstream data", captured.UserMessages);
    }

    [Fact]
    public async Task ExecuteAsync_NotifiesStepStarted()
    {
        var config = new LlmCallConfig { SystemPrompt = "Test.", ModelDeploymentKey = "gpt-4" };
        var step = CreateStep(config);

        _sender.Setup(s => s.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult { Success = true, FinalResponse = "ok", Turns = [] });

        await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        _notifier.Verify(n => n.NotifyStepStartedAsync(
            _context.CurrentPlanId!.Value, step.Id, step.Name, StepType.LlmCall, It.IsAny<CancellationToken>()), Times.Once);
    }
}
