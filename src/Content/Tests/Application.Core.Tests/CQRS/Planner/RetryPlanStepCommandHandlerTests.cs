using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class RetryPlanStepCommandHandlerTests
{
    private readonly Mock<IPlanExecutor> _executorMock = new();
    private readonly Mock<ILogger<RetryPlanStepCommandHandler>> _loggerMock = new();
    private readonly RetryPlanStepCommandHandler _handler;

    public RetryPlanStepCommandHandlerTests()
    {
        _handler = new RetryPlanStepCommandHandler(
            _executorMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Handle_FailedStep_RestartsStep()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var command = new RetryPlanStepCommand { PlanId = planId, StepId = stepId };

        _executorMock
            .Setup(e => e.RetryStepAsync(planId, stepId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _executorMock.Verify(e => e.RetryStepAsync(planId, stepId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NonFailedStep_ReturnsFail()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var command = new RetryPlanStepCommand { PlanId = planId, StepId = stepId };

        _executorMock
            .Setup(e => e.RetryStepAsync(planId, stepId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("Step is not in a failed state"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }
}
