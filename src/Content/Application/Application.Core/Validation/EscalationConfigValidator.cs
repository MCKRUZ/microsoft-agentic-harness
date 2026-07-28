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

        // Deliberately NOT gated on Enabled, unlike the sibling rules below. This setting no
        // longer belongs to escalation alone: the change-proposal decision API reads the same
        // ApproverClaimType to stamp the reviewer identity on its audit records, and that API is
        // mounted independently of EscalationConfig.Enabled (which defaults to false). Gating the
        // allowlist behind one subsystem's flag therefore left the default deployment reading an
        // unvalidated claim type — an operator could point it at a user-editable claim (a B2C
        // custom attribute has no JWT inbound mapping, so it resolves cleanly) and get
        // attacker-chosen strings recorded as the deciding identity. Claim hygiene is an
        // invariant of the claim itself, not a feature of whoever consumes it. Safe to always
        // enforce: the class default (preferred_username) is allowlisted, so hosts running on
        // defaults keep booting.
        RuleFor(x => x.ApproverClaimType)
            .Must(v => ApproverClaimTypes.Allowed.Contains(v, StringComparer.Ordinal))
            .WithMessage(
                "ApproverClaimType must be one of: " + string.Join(", ", ApproverClaimTypes.Allowed) +
                ". Only issuer-asserted identity claims may drive approval identity — " +
                "user-editable claims like 'name' or unverified 'email' must never select the " +
                "approver or be stamped as a change-proposal reviewer.");

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
