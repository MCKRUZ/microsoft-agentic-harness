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

    /// <summary>
    /// The only claim types allowed to drive escalation approver identity. All four are
    /// issuer-asserted: <c>oid</c>/<c>sub</c> are immutable object/subject ids; <c>upn</c> and
    /// <c>preferred_username</c> are sign-in names (mutable — see
    /// <see cref="MutableApproverClaimTypes"/>). Anything user-editable (display name,
    /// unverified email) is rejected at startup so it can never select the approver.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedApproverClaimTypes =
        ["oid", "sub", "preferred_username", "upn"];

    /// <summary>
    /// The allowed claim types that are nonetheless mutable and reassignable (a departed
    /// approver's UPN can be reissued to a new hire, who then inherits roster entries naming
    /// it). Hosts configured with one of these get a startup warning; <c>oid</c> is the
    /// production recommendation.
    /// </summary>
    public static readonly IReadOnlyList<string> MutableApproverClaimTypes =
        ["preferred_username", "upn"];

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

        RuleFor(x => x.ApproverClaimType)
            .Must(v => AllowedApproverClaimTypes.Contains(v, StringComparer.Ordinal))
            .WithMessage(
                "ApproverClaimType must be one of: " + string.Join(", ", AllowedApproverClaimTypes) +
                ". Only issuer-asserted identity claims may drive roster authorization — " +
                "user-editable claims like 'name' or unverified 'email' must never select the approver.");

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
