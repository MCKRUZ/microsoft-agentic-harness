using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Autonomy;
using Domain.AI.Changes;
using Domain.Common.Config.AI.Governance;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ToolApprovalConfig"/> at startup so a misconfigured approval gate is a boot
/// failure rather than a tool call that is silently refused forever.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> Every setting here flows into an
/// <c>EscalationRequest</c>, whose invariants are checked inside the escalation service — which
/// throws, which the router's fail-closed catch converts into a block. The net effect of one bad
/// value is that every approval-required tool call is refused for the life of the process, with
/// nothing to show for it but a per-call error log. That failure mode has already been found three
/// separate times on this config surface (a Quorum strategy with no threshold, a blank roster entry,
/// and an out-of-range timeout). Rejecting at boot converts a silent, permanent, hard-to-diagnose
/// outage into a startup error naming the setting.
/// </para>
/// <para>
/// Rules are gated on <see cref="ToolApprovalConfig.Enabled"/>: a host that has not opted in should
/// not be forced to populate settings it never reads.
/// </para>
/// </remarks>
public sealed class ToolApprovalConfigValidator : AbstractValidator<ToolApprovalConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ToolApprovalConfigValidator"/> class.</summary>
    public ToolApprovalConfigValidator()
    {
        // Unvalidated, this parses to Critical and only narrows who is paged — but it does so
        // silently, and a typo in a governance setting should never be discoverable only by
        // noticing that nobody was notified.
        //
        // Shares AutonomyValidationRules' parser and message rather than restating them: that class
        // exists so every boundary rejecting a blast-radius value rejects the same set and says the
        // same thing, and this is the third such boundary. The local name-list check it replaces was
        // behaviourally equivalent — both reject numeric forms — so this buys shared vocabulary, not
        // stricter parsing. Worth stating plainly, because the reverse is easy to assume.
        //
        // The genuine divergence is elsewhere and is NOT closed here: EscalationToolApprovalRouter
        // parses the same setting with a bare Enum.TryParse, which does accept "3". A validated host
        // never reaches that, since this rule rejects the value at boot; a host that binds
        // GovernanceConfig without registering these options would. Closing it means moving the
        // shared parser down to a layer Application.AI.Common can reference — a separate change.
        RuleFor(x => x.CriticalAtBlastRadius)
            .Must(v => AutonomyValidationRules.TryParseEnumName<BlastRadius>(v, out _))
            .WithMessage($"CriticalAtBlastRadius is invalid. {AutonomyValidationRules.InvalidBlastRadiusMessage}");

        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.Approvers)
                .Must(a => a.Count > 0)
                .WithMessage(
                    "ToolApproval is enabled but Approvers is empty. An escalation with no roster can never be " +
                    "answered, so every approval-required tool call would be refused.");

            RuleFor(x => x.Approvers)
                .Must(a => a.All(name => !string.IsNullOrWhiteSpace(name)))
                .WithMessage("Approvers must not contain blank entries.");

            // Null inherits EscalationConfig.DefaultTimeoutSeconds, which its own validator bounds.
            RuleFor(x => x.TimeoutSeconds!.Value)
                .GreaterThan(0)
                .WithMessage("TimeoutSeconds must be greater than zero — a tool approval nobody can answer is a denial.")
                .LessThanOrEqualTo(EscalationRequestInvariants.MaxTimeoutSeconds)
                .WithMessage(
                    $"TimeoutSeconds must not exceed {EscalationRequestInvariants.MaxTimeoutSeconds}s; " +
                    "the escalation service rejects a longer window, which the approval gate reports as a block.")
                .When(x => x.TimeoutSeconds.HasValue);
        });
    }
}
