using FluentValidation;

namespace Application.AI.Common.CQRS.Bundles.GetBundleRun;

/// <summary>
/// Validates <see cref="GetBundleRunQuery"/> before the handler runs.
/// </summary>
public sealed class GetBundleRunQueryValidator : AbstractValidator<GetBundleRunQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetBundleRunQueryValidator()
    {
        RuleFor(x => x.Handle)
            .NotEmpty().WithMessage("Handle is required.");

        RuleFor(x => x.JobId)
            .NotEmpty().WithMessage("JobId is required.");
    }
}
