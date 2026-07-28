using Domain.AI.DriftDetection;
using FluentValidation;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Validates <see cref="GetDriftHistoryQuery"/>: a defined scope, a bounded scope identifier,
/// and an ordered time window no longer than
/// <see cref="DriftValidationRules.MaxHistoryWindowDays"/> days.
/// </summary>
public sealed class GetDriftHistoryQueryValidator : AbstractValidator<GetDriftHistoryQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetDriftHistoryQueryValidator()
    {
        // A history read is always scoped, so an omitted scope is a bad request — never a
        // default. Binding a missing value to DriftScope.Agent (enum zero) would return real
        // Agent-scope data with a 200 to a caller who believes they asked for something else.
        RuleFor(x => x.Scope)
            .NotNull().WithMessage("Scope is required.")
            .Must(scope => scope is null || Enum.IsDefined(scope.Value))
                .WithMessage("Scope must be a defined DriftScope value.");

        RuleFor(x => x.ScopeIdentifier)
            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.")
            .MaximumLength(DriftValidationRules.MaxScopeIdentifierLength)
                .WithMessage($"ScopeIdentifier must not exceed {DriftValidationRules.MaxScopeIdentifierLength} characters.");

        RuleFor(x => x.Start)
            .LessThan(x => x.End).WithMessage("Start must be before End.");

        RuleFor(x => x)
            .Must(x => (x.End - x.Start) <= TimeSpan.FromDays(DriftValidationRules.MaxHistoryWindowDays))
            .WithMessage($"The query window must not exceed {DriftValidationRules.MaxHistoryWindowDays} days.")
            .When(x => x.Start < x.End)
            .OverridePropertyName(nameof(GetDriftHistoryQuery.End));
    }
}
