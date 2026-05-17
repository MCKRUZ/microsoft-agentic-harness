using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class PlanQueryHandlerTests
{
    private readonly Mock<IPlanStateStore> _storeMock = new();

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

    // --- GetPlanQueryHandler ---

    [Fact]
    public async Task GetPlan_ExistingPlan_ReturnsGraphAndState()
    {
        var loggerMock = new Mock<ILogger<GetPlanQueryHandler>>();
        var handler = new GetPlanQueryHandler(_storeMock.Object, loggerMock.Object);
        var planId = PlanId.New();
        var plan = CreateValidPlanGraph(planId);
        var stepStates = new Dictionary<PlanStepId, StepExecutionState>
        {
            [plan.Steps[0].Id] = new StepExecutionState
            {
                StepId = plan.Steps[0].Id,
                Status = StepExecutionStatus.Completed
            }
        };

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(plan));

        _storeMock
            .Setup(s => s.LoadStepStatesAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(stepStates));

        var query = new GetPlanQuery { PlanId = planId };
        var result = await handler.Handle(query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(plan, result.Value.Graph);
        Assert.Equal(stepStates, result.Value.StepStates);
    }

    [Fact]
    public async Task GetPlan_NonexistentPlan_ReturnsNotFound()
    {
        var loggerMock = new Mock<ILogger<GetPlanQueryHandler>>();
        var handler = new GetPlanQueryHandler(_storeMock.Object, loggerMock.Object);
        var planId = PlanId.New();

        _storeMock
            .Setup(s => s.LoadPlanAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(null));

        var query = new GetPlanQuery { PlanId = planId };
        var result = await handler.Handle(query, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.NotFound, result.FailureType);
    }

    // --- GetPlanHistoryQueryHandler ---

    [Fact]
    public async Task GetPlanHistory_ExecutedPlan_ReturnsAuditTrail()
    {
        var handler = new GetPlanHistoryQueryHandler(_storeMock.Object);
        var planId = PlanId.New();
        var history = new List<PlanExecutionLogEntry>
        {
            new()
            {
                PlanId = planId,
                StepId = PlanStepId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Status = StepExecutionStatus.Completed,
                Message = "Step completed"
            }
        };

        _storeMock
            .Setup(s => s.GetExecutionHistoryAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<PlanExecutionLogEntry>>.Success(history));

        var query = new GetPlanHistoryQuery { PlanId = planId };
        var result = await handler.Handle(query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
    }

    // --- ListPlansQueryHandler ---

    [Fact]
    public async Task ListPlans_WithFilters_ReturnsMatchingPlans()
    {
        var handler = new ListPlansQueryHandler(_storeMock.Object);
        var plans = new List<PlanGraph> { CreateValidPlanGraph() };

        _storeMock
            .Setup(s => s.ListPlansAsync(
                StepExecutionStatus.Completed,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<PlanGraph>>.Success(plans));

        var query = new ListPlansQuery { StatusFilter = StepExecutionStatus.Completed };
        var result = await handler.Handle(query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
    }
}
