using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.CancelChangeProposal;

/// <summary>Validates <see cref="CancelChangeProposalCommand"/>.</summary>
public sealed class CancelChangeProposalCommandValidator : AbstractValidator<CancelChangeProposalCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public CancelChangeProposalCommandValidator()
    {
        RuleFor(x => x.ProposalId).NotEmpty();
        RuleFor(x => x.CancelledBy).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(2000);
    }
}
