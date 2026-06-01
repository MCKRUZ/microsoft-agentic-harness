using FluentValidation;

namespace Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;

/// <summary>Validates <see cref="GetTopRegressedCasesQuery"/>.</summary>
public sealed class GetTopRegressedCasesQueryValidator
    : AbstractValidator<GetTopRegressedCasesQuery>
{
    /// <summary>Maximum rows a single request can ask for.</summary>
    public const int MaxTake = 200;

    /// <summary>Initializes the validator.</summary>
    public GetTopRegressedCasesQueryValidator()
    {
        RuleFor(x => x.CurrentRunId).NotEmpty().WithMessage("CurrentRunId is required.");
        RuleFor(x => x.BaselineRunId).NotEmpty().WithMessage("BaselineRunId is required.");
        RuleFor(x => x).Must(q => q.CurrentRunId != q.BaselineRunId)
            .WithMessage("CurrentRunId and BaselineRunId must differ.");
        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be positive.")
            .LessThanOrEqualTo(MaxTake).WithMessage($"Take must not exceed {MaxTake}.");
    }
}
