using FluentValidation;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;

/// <summary>
/// Validates <see cref="GetEvalRunHistoryQuery"/>: bounds Take to a sane range so
/// a malformed dashboard call can't request 100k rows.
/// </summary>
public sealed class GetEvalRunHistoryQueryValidator : AbstractValidator<GetEvalRunHistoryQuery>
{
    /// <summary>Maximum rows a single history request can ask for. Caps memory + payload.</summary>
    public const int MaxTake = 500;

    /// <summary>Initializes the validator with rules for query shape.</summary>
    public GetEvalRunHistoryQueryValidator()
    {
        RuleFor(x => x.Take)
            .GreaterThan(0).WithMessage("Take must be positive.")
            .LessThanOrEqualTo(MaxTake)
            .WithMessage($"Take must not exceed {MaxTake}.");
    }
}
