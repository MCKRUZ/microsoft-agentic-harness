using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="GovernanceConfig"/> — the parent Agent Governance Toolkit configuration bound
/// from <c>AppConfig:AI:Governance</c>. Its <c>Escalation</c> and <c>DataClassification</c> subsections
/// have their own validators; this one covers the parent-level flags and guards against
/// internally-inconsistent combinations that would otherwise start up silently and behave as no-ops.
/// </summary>
/// <remarks>
/// <para>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly and wired into
/// the options pipeline with <c>ValidateOnStart()</c> in the composition root, so an invalid governance
/// section fails the host at boot rather than at first use.
/// </para>
/// <para>
/// Rules are shaped so a default (or omitted) section always passes: <see cref="GovernanceConfig.Enabled"/>,
/// <see cref="GovernanceConfig.EnablePromptInjectionDetection"/>, and
/// <see cref="GovernanceConfig.EnableMcpSecurity"/> all default to <see langword="false"/>, and the enum /
/// path rules accept the class defaults.
/// </para>
/// </remarks>
public sealed class GovernanceConfigValidator : AbstractValidator<GovernanceConfig>
{
    /// <summary>Initializes a new instance of the <see cref="GovernanceConfigValidator"/> class.</summary>
    public GovernanceConfigValidator()
    {
        // Enum sanity — an out-of-range integer bound from config must not flow through as a silent
        // undefined enum value that later switch/threshold comparisons mishandle.
        RuleFor(x => x.ConflictStrategy)
            .IsInEnum()
            .WithMessage("ConflictStrategy must be a defined ConflictResolutionStrategy value.");

        RuleFor(x => x.InjectionBlockThreshold)
            .IsInEnum()
            .WithMessage("InjectionBlockThreshold must be a defined ThreatLevel value.");

        RuleFor(x => x.ResponseBlockThreshold)
            .IsInEnum()
            .WithMessage("ResponseBlockThreshold must be a defined ThreatLevel value.");

        // A blank policy path can never resolve to a file — the DI registration filters it out via
        // File.Exists, so it never loads. Surface it as a typo rather than silently dropping it.
        RuleForEach(x => x.PolicyPaths)
            .Must(path => !string.IsNullOrWhiteSpace(path))
            .WithMessage(
                "PolicyPaths contains a blank entry. A blank path resolves to no file and is silently " +
                "dropped — remove it or supply the policy file path.");

        // Landmine guard: these sub-features only run through the AGT kernel path, which the composition
        // root wires exclusively when Enabled=true. Turning one on while governance is disabled is a
        // no-op the operator cannot see — the composition root registers the no-op scanner/scanner and
        // the corresponding MediatR behaviour passes through — so the flag never fires. Reject it so the
        // contradiction surfaces at boot instead of masquerading as protection at runtime. (Independent
        // of EnforceToolInvocation, ProgressGuard, and DataClassification.Mode, which are consumed on the
        // live tool path regardless of Enabled and so are intentionally not constrained here.)
        When(x => !x.Enabled, () =>
        {
            RuleFor(x => x.EnablePromptInjectionDetection)
                .Equal(false)
                .WithMessage(
                    "EnablePromptInjectionDetection requires Governance.Enabled=true. With governance " +
                    "disabled the composition root wires the no-op injection scanner and " +
                    "PromptInjectionBehavior passes through, so detection never runs — enable governance " +
                    "or clear this flag.");

            RuleFor(x => x.EnableMcpSecurity)
                .Equal(false)
                .WithMessage(
                    "EnableMcpSecurity requires Governance.Enabled=true. With governance disabled the " +
                    "composition root wires the no-op MCP scanner, so tool-registration scanning never " +
                    "runs — enable governance or clear this flag.");
        });
    }
}
