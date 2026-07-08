using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Verifies that <see cref="SubPlanStepExecutor"/> propagates a child sub-plan's honest outcome to
/// the parent step. A child plan now returns <c>Result.Success</c> even when its summary's
/// <see cref="PlanExecutionSummary.FinalStatus"/> is Failed/Blocked/Cancelled, so keying the parent
/// step's status off <c>childResult.IsSuccess</c> alone would falsely mark the parent Completed.
/// </summary>
public sealed class SubPlanStepExecutorTests
{
    private readonly Mock<IPlanExecutor> _childExecutor = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();

    [Theory]
    [InlineData(StepExecutionStatus.Failed)]
    [InlineData(StepExecutionStatus.Blocked)]
    [InlineData(StepExecutionStatus.Cancelled)]
    public async Task ExecuteAsync_ChildPlanDidNotSucceed_ReturnsFailedNotCompleted(StepExecutionStatus childFinalStatus)
    {
        var childPlanId = PlanId.New();
        var summary = new PlanExecutionSummary
        {
            PlanId = childPlanId,
            FinalStatus = childFinalStatus,
            TotalDuration = TimeSpan.FromSeconds(1),
            StepStates = []
        };
        _childExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(summary));

        var executor = BuildExecutor();
        var step = SubPlanStep(childPlanId);

        var result = await executor.ExecuteAsync(step, EmptyOutputs, CancellationToken.None);

        Assert.NotEqual(StepExecutionStatus.Completed, result.Status);
        Assert.Equal(StepExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ChildPlanCompleted_ReturnsCompleted()
    {
        var childPlanId = PlanId.New();
        var summary = new PlanExecutionSummary
        {
            PlanId = childPlanId,
            FinalStatus = StepExecutionStatus.Completed,
            TotalDuration = TimeSpan.FromSeconds(1),
            StepStates = []
        };
        _childExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<PlanExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(summary));

        var executor = BuildExecutor();
        var step = SubPlanStep(childPlanId);

        var result = await executor.ExecuteAsync(step, EmptyOutputs, CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
    }

    private static readonly IReadOnlyDictionary<PlanStepId, string> EmptyOutputs =
        new Dictionary<PlanStepId, string>();

    private SubPlanStepExecutor BuildExecutor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_childExecutor.Object);
        var provider = services.BuildServiceProvider();

        return new SubPlanStepExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IPlanStateStore>(),
            _notifier.Object,
            new PlanExecutionContext { Depth = 0, MaxDepth = 5 },
            NullLogger<SubPlanStepExecutor>.Instance);
    }

    private static PlanStep SubPlanStep(PlanId childPlanId) => new()
    {
        Id = PlanStepId.New(),
        Name = "sub-plan-step",
        Type = StepType.SubPlanInvocation,
        Configuration = new SubPlanConfig { ChildPlanId = childPlanId },
        RetryPolicy = new RetryPolicy { MaxRetries = 0 }
    };
}
