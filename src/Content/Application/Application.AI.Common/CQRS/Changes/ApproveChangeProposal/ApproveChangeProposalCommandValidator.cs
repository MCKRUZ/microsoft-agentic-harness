using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>Validates <see cref="ApproveChangeProposalCommand"/>.</summary>
public sealed class ApproveChangeProposalCommandValidator : AbstractValidator<ApproveChangeProposalCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public ApproveChangeProposalCommandValidator()
    {
        RuleFor(x => x.ProposalId).NotEmpty();
        RuleFor(x => x.ReviewerId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(2000);
    }
}
