using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.GetChangeProposal;

/// <summary>Validates <see cref="GetChangeProposalQuery"/>.</summary>
public sealed class GetChangeProposalQueryValidator : AbstractValidator<GetChangeProposalQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetChangeProposalQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
