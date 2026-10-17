using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class HumanGateStepExecutorTests
{
    private readonly Mock<IEscalationService> _escalationService = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly PlanId _planId = PlanId.New();
    private readonly HumanGateStepExecutor _sut;

    public HumanGateStepExecutorTests()
    {
        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new HumanGateStepExecutor(
            _escalationService.Object,
            _notifier.Object,
            _context,
            NullLogger<HumanGateStepExecutor>.Instance);
    }

    private static PlanStep CreateStep(HumanGateConfig config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "approval-gate",
        Type = StepType.HumanGate,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "bad",
            Type = StepType.HumanGate,
            Configuration = new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "y" },
            RetryPolicy = new RetryPolicy()
        };

        var result = await _sut.ExecuteAsync(_planId, step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ValidConfig_ReturnsBlocked()
    {
        var escalationId = Guid.NewGuid();
        _escalationService.Setup(s => s.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        var config = new HumanGateConfig
        {
            EscalationMessage = "Approve deployment to prod?",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };
        var step = CreateStep(config);

        var result = await _sut.ExecuteAsync(_planId, step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Blocked, result.Status);
        Assert.Contains(escalationId.ToString(), result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MapsApprovalStrategyCorrectly()
    {
        EscalationRequest? captured = null;
        _escalationService.Setup(s => s.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());

        var config = new HumanGateConfig
        {
            EscalationMessage = "Approve?",
            ApprovalStrategy = ApprovalStrategy.AllOf
        };
        var step = CreateStep(config);

        await _sut.ExecuteAsync(_planId, step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(ApprovalStrategyType.AllOf, captured!.ApprovalStrategy);
    }

    [Fact]
    public async Task ExecuteAsync_TruncatesLongUpstreamOutputs()
    {
        EscalationRequest? captured = null;
        _escalationService.Setup(s => s.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());

        var config = new HumanGateConfig
        {
            EscalationMessage = "Review this data",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var longOutput = new string('x', 500);
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = longOutput };

        await _sut.ExecuteAsync(_planId, step, outputs, CancellationToken.None);

        Assert.NotNull(captured);
        var argValue = captured!.Arguments.Values.First();
        Assert.True(argValue.Length <= 203);
        Assert.EndsWith("...", argValue);
    }

    [Fact]
    public async Task ExecuteAsync_QueuesTheEscalationBoundToItsPlanAndStep()
    {
        EscalationRequest? captured = null;
        _escalationService.Setup(s => s.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());
        var step = CreateStep(new HumanGateConfig { EscalationMessage = "Approve?", ApprovalStrategy = ApprovalStrategy.AnyOf });

        await _sut.ExecuteAsync(_planId, step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        // Only a verdict that names this plan and step may release it (#784).
        Assert.Equal(new EscalationBinding(_planId.Value, step.Id.Value), captured!.Binding);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAPlanId_RefusesToQueueAnUnboundEscalation()
    {
        var step = CreateStep(new HumanGateConfig { EscalationMessage = "Approve?", ApprovalStrategy = ApprovalStrategy.AnyOf });

        // The plain overload knows no plan, so anything it queued would be releasable by any approval.
        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(PlanStepErrors.HumanGateUnbound, result.ErrorMessage);
        _escalationService.Verify(
            s => s.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
