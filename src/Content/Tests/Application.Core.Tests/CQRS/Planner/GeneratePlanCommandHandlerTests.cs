using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class GeneratePlanCommandHandlerTests
{
    private readonly Mock<IPlanGenerator> _generatorMock = new();
    private readonly Mock<IPlanValidator> _validatorMock = new();
    private readonly Mock<IPlanStateStore> _storeMock = new();
    private readonly Mock<ILogger<GeneratePlanCommandHandler>> _loggerMock = new();
    private readonly GeneratePlanCommandHandler _handler;

    public GeneratePlanCommandHandlerTests()
    {
        _handler = new GeneratePlanCommandHandler(
            _generatorMock.Object,
            _validatorMock.Object,
            _storeMock.Object,
            _loggerMock.Object);
    }

    private static PlanGraph CreateValidPlanGraph() => new()
    {
        Id = PlanId.New(),
        Name = "Generated Plan",
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
    public async Task Handle_ValidTask_GeneratesAndPersistsPlan()
    {
        var plan = CreateValidPlanGraph();
        var command = new GeneratePlanCommand { TaskDescription = "Build a parser" };
        var validationResult = new PlanValidationResult
        {
            IsValid = true,
            Errors = [],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.FromMinutes(3)
        };

        _generatorMock
            .Setup(g => g.GenerateAsync("Build a parser", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph>.Success(plan));

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        _storeMock
            .Setup(s => s.SavePlanAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(plan.Id, result.Value);
        _generatorMock.Verify(g => g.GenerateAsync("Build a parser", null, It.IsAny<CancellationToken>()), Times.Once);
        _validatorMock.Verify(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(s => s.SavePlanAsync(plan, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_GeneratorFails_ReturnsFailResult()
    {
        var command = new GeneratePlanCommand { TaskDescription = "Build a parser" };

        _generatorMock
            .Setup(g => g.GenerateAsync("Build a parser", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph>.Fail("LLM generation failed"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _validatorMock.Verify(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
        _storeMock.Verify(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_GeneratedPlanFailsValidation_ReturnsValidationFailure()
    {
        var plan = CreateValidPlanGraph();
        var command = new GeneratePlanCommand { TaskDescription = "Build a parser" };
        var validationResult = new PlanValidationResult
        {
            IsValid = false,
            Errors = ["Generated plan has unreachable steps"],
            Warnings = [],
            EstimatedCriticalPathDuration = TimeSpan.Zero
        };

        _generatorMock
            .Setup(g => g.GenerateAsync("Build a parser", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph>.Success(plan));

        _validatorMock
            .Setup(v => v.ValidateAsync(plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(validationResult));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        _storeMock.Verify(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
