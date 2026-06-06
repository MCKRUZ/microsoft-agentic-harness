using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.RejectChangeProposal;

/// <summary>Validates <see cref="RejectChangeProposalCommand"/>.</summary>
public sealed class RejectChangeProposalCommandValidator : AbstractValidator<RejectChangeProposalCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RejectChangeProposalCommandValidator()
    {
        RuleFor(x => x.ProposalId).NotEmpty();
        RuleFor(x => x.ReviewerId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000)
            .WithMessage("Reject requires a non-empty reason (max 2000 chars) so the submitting agent can react.");
    }
}
