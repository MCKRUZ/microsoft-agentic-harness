using Domain.AI.Escalation;
using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="SubmitEscalationDecisionCommand"/>: a non-empty escalation id, a present
/// and bounded controller-stamped approver name, and a bounded optional reason.
/// </summary>
public sealed class SubmitEscalationDecisionCommandValidator
    : AbstractValidator<SubmitEscalationDecisionCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public SubmitEscalationDecisionCommandValidator()
    {
        RuleFor(x => x.EscalationId)
            .NotEmpty().WithMessage("EscalationId must not be empty.");

        RuleFor(x => x.ApproverName)
            .NotEmpty().WithMessage("ApproverName must not be empty.")
            .MaximumLength(EscalationValidationRules.MaxApproverNameLength)
                .WithMessage($"ApproverName must not exceed {EscalationValidationRules.MaxApproverNameLength} characters.");

        RuleFor(x => x.Reason)
            .MaximumLength(EscalationValidationRules.MaxReasonLength)
                .WithMessage($"Reason must not exceed {EscalationValidationRules.MaxReasonLength} characters.");

        RuleFor(x => x.Instructions)
            .MaximumLength(EscalationValidationRules.MaxInstructionsLength)
                .WithMessage($"Instructions must not exceed {EscalationValidationRules.MaxInstructionsLength} characters.");

        // Rejects an out-of-range Verdict (e.g. a forward-versioned or fat-fingered integer that
        // ASP.NET's default JsonStringEnumConverter binds without complaint — ints are accepted
        // unless a host disables that explicitly). This does not, by itself, close every wire
        // trick (see the Enum.IsDefined limitation documented on EnumNameHelper), but it is
        // strictly better than validating nothing, and it stops an undefined value from reaching
        // the approval strategies at all rather than depending solely on their own fail-closed
        // fallback.
        RuleFor(x => x.Verdict)
            .Must(v => v is null || Enum.IsDefined(v.Value))
            .WithMessage("Verdict is not a recognized value.");

        // A caller supplying both fields must mean one thing. Silently preferring Verdict over a
        // contradicting Approve would let a client bug ship an unintended decision; surfacing it
        // as a validation failure is the fail-closed reading.
        RuleFor(x => x)
            .Must(x => x.Verdict is not { } v || (v == ApproverVerdict.Approve) == x.Approve)
            .WithMessage("Verdict contradicts Approve — a Revise or Deny verdict must be sent with Approve=false.")
            .WithName("Verdict");

        // A Revise verdict with nothing to relay is a deny with extra steps: there is no
        // instruction for the next attempt to act on.
        RuleFor(x => x.Instructions)
            .NotEmpty()
            .WithMessage("Instructions must be provided when Verdict is Revise.")
            .When(x => x.Verdict == ApproverVerdict.Revise);
    }
}
