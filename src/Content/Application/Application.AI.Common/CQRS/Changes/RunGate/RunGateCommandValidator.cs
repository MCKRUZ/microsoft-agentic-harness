using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.RunGate;

/// <summary>Validates <see cref="RunGateCommand"/>.</summary>
public sealed class RunGateCommandValidator : AbstractValidator<RunGateCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RunGateCommandValidator()
    {
        RuleFor(x => x.ProposalId).NotEmpty();
        RuleFor(x => x.GateKey).NotEmpty();
        RuleFor(x => x.AttemptCount).GreaterThanOrEqualTo(1)
            .WithMessage("AttemptCount is 1-based; the first evaluation must use 1.");
        RuleFor(x => x.CorrelationId).NotEmpty();
    }
}
