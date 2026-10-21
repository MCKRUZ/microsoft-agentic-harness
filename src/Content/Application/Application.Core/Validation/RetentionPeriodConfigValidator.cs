using Domain.Common.Config;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates a <see cref="RetentionPeriodConfig"/> at startup (#901). Each section that uses one is bound as a named
/// option with this validator, so a period too short to be a policy stops the host before any sweep deletes anything.
/// </summary>
/// <remarks>
/// Each sweep repeats the floor itself, because a value reloaded while the host runs is not re-validated here.
/// </remarks>
public sealed class RetentionPeriodConfigValidator : AbstractValidator<RetentionPeriodConfig>
{
    /// <summary>Initializes a new instance of the <see cref="RetentionPeriodConfigValidator"/> class.</summary>
    public RetentionPeriodConfigValidator()
    {
        RuleFor(x => x.RetainFor)
            .GreaterThanOrEqualTo(RetentionPeriodConfig.MinimumRetainFor)
            .WithMessage("RetainFor must be at least one day.");

        RuleFor(x => x.SweepInterval)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("SweepInterval must be positive.");
    }
}
