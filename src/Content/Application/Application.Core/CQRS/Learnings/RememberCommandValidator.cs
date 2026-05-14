using FluentValidation;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Validates <see cref="RememberCommand"/>: content not empty, scope valid, source/provenance not null,
/// and provenance confidence in [0, 1].
/// </summary>
public sealed class RememberCommandValidator : AbstractValidator<RememberCommand>
{
    public RememberCommandValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content must not be empty.");

        RuleFor(x => x.Scope)
            .Must(s => s.IsGlobal || !string.IsNullOrEmpty(s.AgentId) || !string.IsNullOrEmpty(s.TeamId))
            .WithMessage("Scope must have AgentId, TeamId, or IsGlobal set.");

        RuleFor(x => x.Source)
            .NotNull().WithMessage("Source must not be null.");

        RuleFor(x => x.Provenance)
            .NotNull().WithMessage("Provenance must not be null.");

        RuleFor(x => x.Provenance.Confidence)
            .InclusiveBetween(0.0, 1.0)
            .When(x => x.Provenance is not null)
            .WithMessage("Provenance.Confidence must be between 0.0 and 1.0.");
    }
}
