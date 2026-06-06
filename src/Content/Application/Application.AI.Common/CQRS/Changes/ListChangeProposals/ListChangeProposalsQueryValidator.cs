using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.ListChangeProposals;

/// <summary>Validates <see cref="ListChangeProposalsQuery"/>.</summary>
public sealed class ListChangeProposalsQueryValidator : AbstractValidator<ListChangeProposalsQuery>
{
    /// <summary>Hard upper bound on MaxResults to protect store implementations from runaway scans.</summary>
    public const int AbsoluteMaxResults = 10_000;

    /// <summary>Initializes validation rules.</summary>
    public ListChangeProposalsQueryValidator()
    {
        RuleFor(x => x.Filter).NotNull();
        RuleFor(x => x.Filter.MaxResults)
            .GreaterThan(0).WithMessage("MaxResults must be positive.")
            .LessThanOrEqualTo(AbsoluteMaxResults)
                .WithMessage($"MaxResults exceeds the absolute upper bound of {AbsoluteMaxResults}.")
            .When(x => x.Filter is not null);
    }
}
