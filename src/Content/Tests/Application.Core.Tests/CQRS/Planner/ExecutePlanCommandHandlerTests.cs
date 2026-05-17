using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class ExecutePlanCommandHandlerTests
{
    private readonly Mock<IPlanStateStore> _storeMock = new();
    private readonly Mock<IPlanValidator> _validatorMock = new();
    private readonly Mock<IPlanExecutor> _executorMock = new();
    private readonly Mock<ILogger<ExecutePlanCommandHandler>> _loggerMock = new();
    private readonly ExecutePlanCommandHandler _handler;

    public ExecutePlanCommandHandlerTests()
    {
        _handler = new ExecutePlanCommandHandler(
            _storeMock.Object,
            _validatorMock.Object,
            _executorMock.Object,
            _loggerMock.Object);
    }

    private static PlanGraph CreateValidPlanGraph(PlanId? planId = null) => new()
    {
        Id = planId ?? PlanId.New(),
        Name = "Test Plan",
        Steps = [new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Step 1",
            Type = StepType.ToolUse,
            Configuration = new ToolUseConfig { ToolName = "test_tool" },
            RetryPolicy = new RetryPolicy()
        }],
        Edges = [],
        Configuration = new PlanConfiguration()
    };

    [Fact]
    public async Task Handle_NewPlan_StartsExecution()
    {
        var planId = PlanId.New();
        var plan = CreateValidPlanGraph(planId);
        var command = new ExecutePlanCommand { PlanId = planId };
        var validationResult = new PlanValidationResult
        {
            IsValid = true,
            Errors = [],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.FromMinutes(5)
        };
        var summary = new PlanExecutionSummary
        {
            PlanId = planId,
            FinalStatus = StepExecutionStatus.Completed,
            TotalDuration = TimeSpan.FromSeconds(30),
            StepStates = [],
            CompletedStepCount = 1,
            FailedStepCount = 0,
            SkippedStepCount = 0
        };

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        _executorMock
            .Setup(e => e.ExecuteAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(summary));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(planId, result.Value.PlanId);
        _executorMock.Verify(e => e.ExecuteAsync(planId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PlanNotFound_ReturnsNotFound()
    {
        var planId = PlanId.New();
        var command = new ExecutePlanCommand { PlanId = planId };

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(null));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.NotFound, result.FailureType);
        _executorMock.Verify(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PlanFailsValidation_ReturnsValidationFailure()
    {
        var planId = PlanId.New();
        var plan = CreateValidPlanGraph(planId);
        var command = new ExecutePlanCommand { PlanId = planId };
        var validationResult = new PlanValidationResult
        {
            IsValid = false,
            Errors = ["Plan has cycles"],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.Zero
        };

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _executorMock.Verify(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_StoreLoadFailure_ReturnsFailResult()
    {
        var planId = PlanId.New();
        var command = new ExecutePlanCommand { PlanId = planId };

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Fail("Database connection lost"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _executorMock.Verify(e => e.ExecuteAsync(It.IsAny<PlanId>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
