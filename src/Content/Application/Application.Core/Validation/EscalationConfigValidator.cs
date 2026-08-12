using Application.AI.Common.Interfaces.Escalation;
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

        // Secondary, boot-time form of the invariant EscalationRequestInvariants.TryValidate
        // enforces per-request. DefaultTimeoutAction has no per-priority override — it applies
        // globally — so a host that configures "Approve" while running Critical escalations
        // (i.e. Critical appears in PriorityLevels) has configured every Critical escalation to
        // auto-approve on timeout unless some other caller overrides TimeoutAction explicitly.
        // Catching it here turns a silent per-request rejection into a startup error that names
        // the setting, rather than an operator discovering it only when a request is refused.
        //
        // Scope: this can only see the GLOBAL default. A caller that sets TimeoutAction on an
        // individual EscalationRequest — bypassing DefaultTimeoutAction entirely — produces the
        // same Critical+Approve pairing invisibly to this rule; that request is caught only by
        // EscalationRequestInvariants at request time (which throws rather than failing to boot).
        // A clean boot here means the pairing can't happen via the default, not that it can't
        // happen at all.
        RuleFor(x => x)
            .Must(c => !CriticalPriorityAutoApprovesOnTimeout(c))
            .WithMessage(
                "DefaultTimeoutAction is 'Approve' while PriorityLevels configures 'Critical' — a " +
                "Critical escalation must never auto-approve on timeout. Change DefaultTimeoutAction " +
                "or remove the Critical entry.")
            .WithName("DefaultTimeoutAction");

        // Ties the #325 retry-attribution card's soft, operator-configured display cap to
        // EscalationRequestInvariants' hard runtime ceiling, so the two can never disagree. A soft
        // cap above the hard ceiling is not a display-only mistake: EscalationToolApprovalRouter
        // truncates defensively to the hard ceiling regardless (see
        // EscalationToolApprovalRouter.TruncatePriorFailureReason), so the practical effect of a
        // misconfigured value here is a silently shorter card, not a broken one — but it is still a
        // configuration error worth surfacing at boot rather than masking behind that fallback.
        RuleFor(x => x.RetryAttribution.MaxPriorFailureLength)
            .InclusiveBetween(1, EscalationRequestInvariants.MaxPriorFailureReasonLength)
            .WithMessage(
                "RetryAttribution.MaxPriorFailureLength must be between 1 and " +
                $"{EscalationRequestInvariants.MaxPriorFailureReasonLength} — " +
                "EscalationRequestInvariants' hard ceiling on EscalationRequest.PriorFailureReason.");

        // Ties the #321 revision-round cap to EscalationRequestInvariants' absolute ceiling on
        // EscalationRequest.RevisionRound, the same pattern as RetryAttribution above: a
        // configured cap higher than the runtime ceiling could never be reached in practice (the
        // ceiling would fail-close first), which is a configuration error worth surfacing at boot
        // rather than a live escalation discovering it.
        RuleFor(x => x.Revision.MaxRounds)
            .InclusiveBetween(1, EscalationRequestInvariants.MaxRevisionRound)
            .WithMessage(
                "Revision.MaxRounds must be between 1 and " +
                $"{EscalationRequestInvariants.MaxRevisionRound} — " +
                "EscalationRequestInvariants' absolute ceiling on EscalationRequest.RevisionRound.");
    }

    private static bool CriticalPriorityAutoApprovesOnTimeout(EscalationConfig config) =>
        config.Enabled &&
        string.Equals(config.DefaultTimeoutAction, nameof(EscalationTimeoutAction.Approve),
            StringComparison.OrdinalIgnoreCase) &&
        config.PriorityLevels.Keys.Any(k =>
            string.Equals(k, nameof(EscalationPriority.Critical), StringComparison.OrdinalIgnoreCase));
}
