using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="RetrievalStepConfiguration"/>.
/// </summary>
/// <remarks>
/// Added closing #526: <c>PlanValidator.ValidateStepConfigurations</c>'s switch had no arm for this
/// type at all — not an unregistered validator for a known type, but a step type with no case in the
/// switch and no validator anywhere in the codebase. Every plan containing a retrieval step reached
/// the switch's <c>_ =&gt; LogUnknownConfigType</c> fallback and passed unchecked, silently, for as
/// long as <see cref="Domain.AI.Planner.StepType.Retrieval"/> has existed. Query is the one field
/// worth a rule: it can carry an upstream-output placeholder that resolves at execution time, so a
/// literal check for a non-empty string after trimming is the validation this type can meaningfully
/// do ahead of execution — checking placeholder resolvability would require the upstream step graph,
/// which is a different concern already covered by <c>ValidateEdgeReferentialIntegrity</c>.
/// </remarks>
public sealed class RetrievalStepConfigValidator : AbstractValidator<RetrievalStepConfiguration>
{
    /// <summary>Initializes a new instance of the <see cref="RetrievalStepConfigValidator"/> class.</summary>
    public RetrievalStepConfigValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty()
            .WithMessage("RetrievalStepConfiguration.Query must not be empty.");

        RuleFor(x => x.TopK)
            .GreaterThan(0)
            .When(x => x.TopK.HasValue)
            .WithMessage("RetrievalStepConfiguration.TopK, when specified, must be greater than zero.");
    }
}
