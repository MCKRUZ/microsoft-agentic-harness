using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Extended validator tests for <see cref="RunOrchestratedTaskCommandValidator"/>,
/// covering boundary values and additional edge cases.
/// </summary>
public class RunOrchestratedTaskCommandValidatorTests_Extended
{
    private readonly RunOrchestratedTaskCommandValidator _validator = new();

    private static RunOrchestratedTaskCommand CreateValidCommand() => new()
    {
        OrchestratorName = "OrchestratorAgent",
        TaskDescription = "Analyze the codebase.",
        AvailableAgents = ["ResearchAgent"],
        MaxTotalTurns = 20
    };

    [Fact]
    public async Task Validate_MaxTotalTurnsExactlyOne_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxTotalTurnsExactly200_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 200 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NegativeMaxTotalTurns_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = -1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTotalTurns");
    }

    [Fact]
    public async Task Validate_WhitespaceOnlyOrchestratorName_FailsValidation()
    {
        var command = CreateValidCommand() with { OrchestratorName = "   " };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "OrchestratorName");
    }

    [Fact]
    public async Task Validate_NullOrchestratorName_FailsValidation()
    {
        var command = CreateValidCommand() with { OrchestratorName = null! };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "OrchestratorName");
    }

    [Fact]
    public async Task Validate_WhitespaceOnlyTaskDescription_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = "   " };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "TaskDescription");
    }

    [Fact]
    public async Task Validate_NullTaskDescription_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = null! };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "TaskDescription");
    }

    [Fact]
    public async Task Validate_TaskDescriptionExactly50KB_PassesValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = new string('x', 50_000) };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SingleAvailableAgent_PassesValidation()
    {
        var command = CreateValidCommand() with { AvailableAgents = ["OnlyAgent"] };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ManyAvailableAgents_PassesValidation()
    {
        var agents = Enumerable.Range(0, 20).Select(i => $"Agent{i}").ToList();
        var command = CreateValidCommand() with { AvailableAgents = agents };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MultipleValidationFailures_ReportsAll()
    {
        var command = CreateValidCommand() with
        {
            OrchestratorName = "",
            TaskDescription = "",
            AvailableAgents = (IReadOnlyList<string>)[],
            MaxTotalTurns = 0
        };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(4);
    }
}
