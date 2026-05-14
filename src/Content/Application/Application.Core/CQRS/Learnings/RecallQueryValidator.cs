using FluentValidation;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Validates <see cref="RecallQuery"/>: context not empty, MaxResults positive, MinRelevance in [0,1].
/// </summary>
public sealed class RecallQueryValidator : AbstractValidator<RecallQuery>
{
    public RecallQueryValidator()
    {
        RuleFor(x => x.Context)
            .NotEmpty().WithMessage("Context must not be empty.");

        RuleFor(x => x.MaxResults)
            .GreaterThan(0).WithMessage("MaxResults must be greater than 0.");

        RuleFor(x => x.MinRelevance)
            .InclusiveBetween(0.0, 1.0).WithMessage("MinRelevance must be between 0.0 and 1.0.");

        RuleFor(x => x.Scope)
            .Must(s => s.IsGlobal || !string.IsNullOrEmpty(s.AgentId) || !string.IsNullOrEmpty(s.TeamId))
            .WithMessage("Scope must have AgentId, TeamId, or IsGlobal set.");
    }
}
