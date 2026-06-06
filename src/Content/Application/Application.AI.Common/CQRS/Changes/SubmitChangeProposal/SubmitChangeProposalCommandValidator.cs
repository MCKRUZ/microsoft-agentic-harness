using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.SubmitChangeProposal;

/// <summary>
/// Validates <see cref="SubmitChangeProposalCommand"/> before the handler runs.
/// </summary>
public sealed class SubmitChangeProposalCommandValidator : AbstractValidator<SubmitChangeProposalCommand>
{
    /// <summary>Upper bound on the summary length to keep audit lines and approval prompts short.</summary>
    public const int MaxSummaryLength = 500;

    /// <summary>Upper bound on diff edit count — runaway diffs are almost always agent bugs.</summary>
    public const int MaxEdits = 1000;

    /// <summary>Upper bound on a single edit's content length.</summary>
    public const int MaxEditContentLength = 1_048_576;

    /// <summary>Initializes validation rules.</summary>
    public SubmitChangeProposalCommandValidator()
    {
        RuleFor(x => x.Target)
            .NotNull().WithMessage("Target is required.");

        RuleFor(x => x.Diff)
            .NotNull().WithMessage("Diff is required.")
            .Must(d => d is null || d.Count > 0).WithMessage("Diff must contain at least one edit.")
            .Must(d => d is null || d.Count <= MaxEdits)
                .WithMessage($"Diff exceeds {MaxEdits} edits — likely an agent loop bug.");

        RuleForEach(x => x.Diff)
            .ChildRules(edit =>
            {
                edit.RuleFor(e => e.Content)
                    .NotNull()
                    .MaximumLength(MaxEditContentLength)
                        .WithMessage($"Edit content exceeds {MaxEditContentLength} characters.");
                edit.RuleFor(e => e.Target)
                    .NotNull()
                    .MaximumLength(MaxEditContentLength)
                        .WithMessage($"Edit target exceeds {MaxEditContentLength} characters.");
            });

        RuleFor(x => x.Summary)
            .NotEmpty().WithMessage("Summary is required.")
            .MaximumLength(MaxSummaryLength)
                .WithMessage($"Summary exceeds {MaxSummaryLength} characters.");

        RuleFor(x => x.RequiredGates)
            .Must(g => g is null || g.Count > 0)
                .WithMessage("RequiredGates, when supplied, must not be empty (omit it to use the resolver default).");

        RuleForEach(x => x.RequiredGates)
            .NotEmpty().WithMessage("Gate keys must be non-empty.");
    }
}
