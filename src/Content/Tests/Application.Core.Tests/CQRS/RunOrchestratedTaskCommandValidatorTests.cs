using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class RunOrchestratedTaskCommandValidatorTests
{
    private readonly RunOrchestratedTaskCommandValidator _validator = new();

    private static RunOrchestratedTaskCommand CreateValidCommand() => new()
    {
        OrchestratorName = "OrchestratorAgent",
        TaskDescription = "Analyze the codebase and produce a summary report.",
        AvailableAgents = ["ResearchAgent", "CodeReviewAgent"],
        MaxTotalTurns = 20
    };

    [Fact]
    public async Task Validate_EmptyOrchestratorName_FailsValidation()
    {
        var command = CreateValidCommand() with { OrchestratorName = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "OrchestratorName");
    }

    [Fact]
    public async Task Validate_EmptyTaskDescription_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "TaskDescription");
    }

    [Fact]
    public async Task Validate_TaskDescriptionOver50KB_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = new string('x', 50_001) };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == "TaskDescription" &&
            e.ErrorMessage.Contains("maximum length"));
    }

    [Fact]
    public async Task Validate_EmptyAvailableAgents_FailsValidation()
    {
        var command = CreateValidCommand() with { AvailableAgents = [] };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AvailableAgents");
    }

    [Fact]
    public async Task Validate_MaxTotalTurnsBelowOne_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 0 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTotalTurns");
    }

    [Fact]
    public async Task Validate_MaxTotalTurnsAbove200_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 201 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTotalTurns");
    }

    [Fact]
    public async Task Validate_ValidCommand_PassesValidation()
    {
        var command = CreateValidCommand();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
