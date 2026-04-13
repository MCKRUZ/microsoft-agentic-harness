using FluentValidation;

namespace Application.Core.CQRS.MetaHarness;

/// <summary>
/// FluentValidation validator for <see cref="RunHarnessOptimizationCommand"/>.
/// Registered automatically via MediatR assembly scanning in
/// <see cref="DependencyInjection.AddApplicationCoreDependencies"/>.
/// </summary>
public sealed class RunHarnessOptimizationCommandValidator
    : AbstractValidator<RunHarnessOptimizationCommand>
{
    /// <summary>Initializes validation rules for <see cref="RunHarnessOptimizationCommand"/>.</summary>
    public RunHarnessOptimizationCommandValidator()
    {
        RuleFor(x => x.OptimizationRunId)
            .NotEqual(Guid.Empty)
            .WithMessage("OptimizationRunId must not be empty.");

        RuleFor(x => x.MaxIterations)
            .GreaterThan(0)
            .WithMessage("MaxIterations must be greater than zero when specified.")
            .When(x => x.MaxIterations.HasValue);
    }
}
