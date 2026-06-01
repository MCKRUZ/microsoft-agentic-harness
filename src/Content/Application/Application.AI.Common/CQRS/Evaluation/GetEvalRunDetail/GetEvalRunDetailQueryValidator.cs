using FluentValidation;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunDetail;

/// <summary>Validates <see cref="GetEvalRunDetailQuery"/>.</summary>
public sealed class GetEvalRunDetailQueryValidator : AbstractValidator<GetEvalRunDetailQuery>
{
    /// <summary>Initializes the validator.</summary>
    public GetEvalRunDetailQueryValidator()
    {
        RuleFor(x => x.RunId).NotEmpty().WithMessage("RunId is required.");
    }
}
