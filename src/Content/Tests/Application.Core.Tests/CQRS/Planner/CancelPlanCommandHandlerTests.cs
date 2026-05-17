using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class CancelPlanCommandHandlerTests
{
    private readonly Mock<IPlanExecutor> _executorMock = new();
    private readonly Mock<ILogger<CancelPlanCommandHandler>> _loggerMock = new();
    private readonly CancelPlanCommandHandler _handler;

    public CancelPlanCommandHandlerTests()
    {
        _handler = new CancelPlanCommandHandler(
            _executorMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Handle_RunningPlan_CancelsSuccessfully()
    {
        var planId = PlanId.New();
        var command = new CancelPlanCommand { PlanId = planId };

        _executorMock
            .Setup(e => e.CancelAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _executorMock.Verify(e => e.CancelAsync(planId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NonexistentPlan_ReturnsFailResult()
    {
        var planId = PlanId.New();
        var command = new CancelPlanCommand { PlanId = planId };

        _executorMock
            .Setup(e => e.CancelAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("Plan not found or not running"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }
}
