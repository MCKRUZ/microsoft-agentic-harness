using Domain.AI.Escalation;
using Domain.Common.Config.AI.Governance;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="EscalationConfig"/> ensuring timeouts are non-negative,
/// priority levels are configured when enabled, and enum string values are valid.
/// </summary>
public sealed class EscalationConfigValidator : AbstractValidator<EscalationConfig>
{
    private static readonly string[] ValidTimeoutActions =
        Enum.GetNames<EscalationTimeoutAction>();

    private static readonly string[] ValidApprovalStrategies =
        Enum.GetNames<ApprovalStrategyType>();

    private static readonly string[] ValidPriorityNames =
        Enum.GetNames<EscalationPriority>();

    public EscalationConfigValidator()
    {
        RuleFor(x => x.DefaultTimeoutSeconds)
            .GreaterThanOrEqualTo(0)
            .WithMessage("DefaultTimeoutSeconds must be >= 0 (zero is valid for informational-only).");

        RuleFor(x => x.DefaultTimeoutAction)
            .Must(v => ValidTimeoutActions.Contains(v, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"DefaultTimeoutAction must be one of: {string.Join(", ", ValidTimeoutActions)}.");

        RuleFor(x => x.DefaultApprovalStrategy)
            .Must(v => ValidApprovalStrategies.Contains(v, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"DefaultApprovalStrategy must be one of: {string.Join(", ", ValidApprovalStrategies)}.");

        RuleFor(x => x.AuditStoragePath)
            .NotEmpty()
            .WithMessage("AuditStoragePath must be configured.");

        // Gated on Enabled like the sibling rules: while the escalation subsystem is off there
        // is nothing the claim type can authorize, and these validators impose no constraints on
        // disabled features (hosts booting on class defaults must keep booting).
        RuleFor(x => x.ApproverClaimType)
            .Must(v => ApproverClaimTypes.Allowed.Contains(v, StringComparer.Ordinal))
            .WithMessage(
                "ApproverClaimType must be one of: " + string.Join(", ", ApproverClaimTypes.Allowed) +
                ". Only issuer-asserted identity claims may drive roster authorization — " +
                "user-editable claims like 'name' or unverified 'email' must never select the approver.")
            .When(x => x.Enabled);

        RuleFor(x => x.PriorityLevels)
            .NotEmpty()
            .WithMessage("PriorityLevels must be configured when escalation is enabled.")
            .When(x => x.Enabled);

        RuleForEach(x => x.PriorityLevels)
            .ChildRules(entry =>
            {
                entry.RuleFor(kv => kv.Key)
                    .Must(k => ValidPriorityNames.Contains(k, StringComparer.OrdinalIgnoreCase))
                    .WithMessage("PriorityLevels key must be a valid EscalationPriority name: " +
                                 string.Join(", ", ValidPriorityNames) + ".");

                entry.RuleFor(kv => kv.Value.TimeoutSeconds)
                    .GreaterThanOrEqualTo(0)
                    .WithMessage("PriorityLevels[{PropertyName}].TimeoutSeconds must be >= 0.");
            });
    }
}
