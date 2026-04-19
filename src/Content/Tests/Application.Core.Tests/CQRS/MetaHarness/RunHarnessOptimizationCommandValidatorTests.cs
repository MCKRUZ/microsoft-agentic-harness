using Application.Core.CQRS.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Unit tests for <see cref="RunHarnessOptimizationCommandValidator"/>.
/// Validates all FluentValidation rules on <see cref="RunHarnessOptimizationCommand"/>.
/// </summary>
public class RunHarnessOptimizationCommandValidatorTests
{
    private readonly RunHarnessOptimizationCommandValidator _validator = new();

    private static RunHarnessOptimizationCommand CreateValidCommand() => new()
    {
        OptimizationRunId = Guid.NewGuid()
    };

    [Fact]
    public async Task Validate_ValidCommand_PassesValidation()
    {
        var command = CreateValidCommand();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EmptyOptimizationRunId_FailsValidation()
    {
        var command = CreateValidCommand() with { OptimizationRunId = Guid.Empty };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "OptimizationRunId");
    }

    [Fact]
    public async Task Validate_MaxIterationsNull_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = null };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxIterationsPositive_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = 5 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxIterationsOne_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = 1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxIterationsZero_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = 0 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxIterations");
    }

    [Fact]
    public async Task Validate_MaxIterationsNegative_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = -1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxIterations");
    }

    [Fact]
    public async Task Validate_MaxIterationsLargePositive_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxIterations = 1000 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EmptyRunIdAndZeroIterations_ReportsMultipleErrors()
    {
        var command = new RunHarnessOptimizationCommand
        {
            OptimizationRunId = Guid.Empty,
            MaxIterations = 0
        };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Errors.Should().Contain(e => e.PropertyName == "OptimizationRunId");
        result.Errors.Should().Contain(e => e.PropertyName == "MaxIterations");
    }

    [Fact]
    public async Task Validate_SeedCandidateIdPresent_DoesNotAffectValidation()
    {
        var command = CreateValidCommand() with { SeedCandidateId = Guid.NewGuid() };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
