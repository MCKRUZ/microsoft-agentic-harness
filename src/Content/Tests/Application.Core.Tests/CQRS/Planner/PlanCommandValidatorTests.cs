using Application.Core.CQRS.Planner;
using Domain.AI.Planner;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Planner;

public sealed class PlanCommandValidatorTests
{
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

    // --- GeneratePlanCommandValidator ---

    [Fact]
    public void Validate_GeneratePlanCommand_ValidInput_NoErrors()
    {
        var validator = new GeneratePlanCommandValidator();
        var command = new GeneratePlanCommand { TaskDescription = "Build a web scraper" };

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_GeneratePlanCommand_EmptyTaskDescription_HasError(string? taskDescription)
    {
        var validator = new GeneratePlanCommandValidator();
        var command = new GeneratePlanCommand { TaskDescription = taskDescription! };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.TaskDescription);
    }

    // --- CreatePlanCommandValidator ---

    [Fact]
    public void Validate_CreatePlanCommand_ValidInput_NoErrors()
    {
        var validator = new CreatePlanCommandValidator();
        var command = new CreatePlanCommand { Plan = CreateValidPlanGraph() };

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_CreatePlanCommand_NullGraph_HasError()
    {
        var validator = new CreatePlanCommandValidator();
        var command = new CreatePlanCommand { Plan = null! };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Plan);
    }

    // --- ExecutePlanCommandValidator ---

    [Fact]
    public void Validate_ExecutePlanCommand_ValidPlanId_NoErrors()
    {
        var validator = new ExecutePlanCommandValidator();
        var command = new ExecutePlanCommand { PlanId = PlanId.New() };

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ExecutePlanCommand_EmptyPlanId_HasError()
    {
        var validator = new ExecutePlanCommandValidator();
        var command = new ExecutePlanCommand { PlanId = new PlanId(Guid.Empty) };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.PlanId);
    }

    // --- CancelPlanCommandValidator ---

    [Fact]
    public void Validate_CancelPlanCommand_ValidPlanId_NoErrors()
    {
        var validator = new CancelPlanCommandValidator();
        var command = new CancelPlanCommand { PlanId = PlanId.New() };

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_CancelPlanCommand_EmptyPlanId_HasError()
    {
        var validator = new CancelPlanCommandValidator();
        var command = new CancelPlanCommand { PlanId = new PlanId(Guid.Empty) };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.PlanId);
    }

    // --- RetryPlanStepCommandValidator ---

    [Fact]
    public void Validate_RetryPlanStepCommand_ValidIds_NoErrors()
    {
        var validator = new RetryPlanStepCommandValidator();
        var command = new RetryPlanStepCommand { PlanId = PlanId.New(), StepId = PlanStepId.New() };

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_RetryPlanStepCommand_EmptyPlanId_HasError()
    {
        var validator = new RetryPlanStepCommandValidator();
        var command = new RetryPlanStepCommand { PlanId = new PlanId(Guid.Empty), StepId = PlanStepId.New() };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.PlanId);
    }

    [Fact]
    public void Validate_RetryPlanStepCommand_EmptyStepId_HasError()
    {
        var validator = new RetryPlanStepCommandValidator();
        var command = new RetryPlanStepCommand { PlanId = PlanId.New(), StepId = new PlanStepId(Guid.Empty) };

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.StepId);
    }

    // --- GetPlanQueryValidator ---

    [Fact]
    public void Validate_GetPlanQuery_ValidPlanId_NoErrors()
    {
        var validator = new GetPlanQueryValidator();
        var query = new GetPlanQuery { PlanId = PlanId.New() };

        var result = validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_GetPlanQuery_EmptyPlanId_HasError()
    {
        var validator = new GetPlanQueryValidator();
        var query = new GetPlanQuery { PlanId = new PlanId(Guid.Empty) };

        var result = validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.PlanId);
    }

    // --- GetPlanHistoryQueryValidator ---

    [Fact]
    public void Validate_GetPlanHistoryQuery_ValidPlanId_NoErrors()
    {
        var validator = new GetPlanHistoryQueryValidator();
        var query = new GetPlanHistoryQuery { PlanId = PlanId.New() };

        var result = validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_GetPlanHistoryQuery_EmptyPlanId_HasError()
    {
        var validator = new GetPlanHistoryQueryValidator();
        var query = new GetPlanHistoryQuery { PlanId = new PlanId(Guid.Empty) };

        var result = validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.PlanId);
    }

    // --- ListPlansQueryValidator ---

    [Fact]
    public void Validate_ListPlansQuery_ValidNoFilters_NoErrors()
    {
        var validator = new ListPlansQueryValidator();
        var query = new ListPlansQuery();

        var result = validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ListPlansQuery_ValidDateRange_NoErrors()
    {
        var validator = new ListPlansQueryValidator();
        var query = new ListPlansQuery
        {
            From = DateTimeOffset.UtcNow.AddDays(-7),
            To = DateTimeOffset.UtcNow
        };

        var result = validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ListPlansQuery_FromAfterTo_HasError()
    {
        var validator = new ListPlansQueryValidator();
        var query = new ListPlansQuery
        {
            From = DateTimeOffset.UtcNow,
            To = DateTimeOffset.UtcNow.AddDays(-7)
        };

        var result = validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.To);
    }
}
