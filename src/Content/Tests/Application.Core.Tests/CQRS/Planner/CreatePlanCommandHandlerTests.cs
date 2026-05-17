using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class CreatePlanCommandHandlerTests
{
    private readonly Mock<IPlanValidator> _validatorMock = new();
    private readonly Mock<IPlanStateStore> _storeMock = new();
    private readonly Mock<ILogger<CreatePlanCommandHandler>> _loggerMock = new();
    private readonly CreatePlanCommandHandler _handler;

    public CreatePlanCommandHandlerTests()
    {
        _handler = new CreatePlanCommandHandler(
            _validatorMock.Object,
            _storeMock.Object,
            _loggerMock.Object);
    }

    private static PlanGraph CreateValidPlanGraph() => new()
    {
        Id = PlanId.New(),
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
    public async Task Handle_ValidPlan_PersistsAndReturnsPlanId()
    {
        var plan = CreateValidPlanGraph();
        var command = new CreatePlanCommand { Plan = plan };
        var validationResult = new PlanValidationResult
        {
            IsValid = true,
            Errors = [],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.FromMinutes(5)
        };

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        _storeMock
            .Setup(s => s.SavePlanAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(plan.Id, result.Value);
        _storeMock.Verify(s => s.SavePlanAsync(plan, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_InvalidPlan_ReturnsValidationFailure()
    {
        var plan = CreateValidPlanGraph();
        var command = new CreatePlanCommand { Plan = plan };
        var validationResult = new PlanValidationResult
        {
            IsValid = false,
            Errors = ["Cycle detected in step graph"],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.Zero
        };

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _storeMock.Verify(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidatorInfraFailure_ReturnsFailResult()
    {
        var plan = CreateValidPlanGraph();
        var command = new CreatePlanCommand { Plan = plan };

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Fail("Validator service unavailable"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _storeMock.Verify(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
