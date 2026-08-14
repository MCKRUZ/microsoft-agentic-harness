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

        // Without this rule an out-of-range value degrades MCP scanning to report-only in the worst
        // possible way: the scan still runs and still logs, but "highest >= threshold" is false for
        // every finding, so nothing is ever withheld while the config says EnableMcpSecurity=true.
        RuleFor(x => x.McpToolBlockThreshold)
            .IsInEnum()
            .WithMessage("McpToolBlockThreshold must be a defined ThreatLevel value.");

        // A blank policy path can never resolve to a file. AddGovernanceDependencies now throws on any
        // configured path (blank or not) that doesn't resolve to a real file (#384) — but that call runs
        // eagerly during service registration (BuildGlobalSolutionServices), before ValidateOnStart()'s
        // deferred check ever runs at IHost.StartAsync(), so in practice the construction-time exception
        // fires first for this exact case. This rule still adds value for any caller that validates
        // GovernanceConfig without going through AddGovernanceDependencies at all.
        RuleForEach(x => x.PolicyPaths)
            .Must(path => !string.IsNullOrWhiteSpace(path))
            .WithMessage(
                "PolicyPaths contains a blank entry. A blank path never resolves to a file — remove it " +
                "or supply the policy file path.");

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

        // The same landmine, for the behaviour posture — but stated precisely, because the imprecise
        // version of this sentence is wrong. The posture is applied by IToolInvocationGovernor, and
        // GovernanceEnforcement.IsActive arms that governor on EITHER EnforceToolInvocation OR an
        // ambient capability envelope. So with enforcement off the posture is not inert everywhere: it
        // still applies inside a bundle run and nowhere else, which is a worse outcome than either
        // consistent answer — the same tool is gated or not depending on how the call arrived, and
        // nothing in the configuration says so.
        When(x => x.ToolBehaviorGating.RequireApprovalForNonReadOnlyTools, () =>
        {
            RuleFor(x => x.EnforceToolInvocation)
                .Equal(true)
                .WithMessage(
                    "ToolBehaviorGating.RequireApprovalForNonReadOnlyTools requires " +
                    "Governance.EnforceToolInvocation=true. The posture is applied by the tool " +
                    "governor, which engages either when invocation enforcement is on or when a " +
                    "bundle run publishes a capability envelope — so leaving enforcement off does not " +
                    "switch the posture off, it applies it to bundle runs alone while every agent " +
                    "turn and plan step goes ungated. Note also that reaching a human needs " +
                    "Governance.ToolApproval.Enabled and Governance.Escalation.Enabled; without them " +
                    "a gated tool call is refused rather than asked about, which is safe but will " +
                    "present to users as tools failing for no stated reason.");
        });

        // Same landmine as EnablePromptInjectionDetection/EnableMcpSecurity above, one level down: the
        // cross-server structural scan (collision/shadowing/drift) that StrictDriftMode tunes only
        // runs when EnableMcpSecurity is true — see ToolChainBuilder.Surface.cs's
        // ResolveSurvivingTools. An operator who turns StrictDriftMode on believing drift is now
        // withheld, while MCP security scanning itself is off, gets a drifted tool definition admitted
        // silently — the same "operator believes a control is on because a related flag is on" trap
        // this file already guards against above.
        When(x => x.McpToolSurfaceScanning.StrictDriftMode, () =>
        {
            RuleFor(x => x.EnableMcpSecurity)
                .Equal(true)
                .WithMessage(
                    "McpToolSurfaceScanning.StrictDriftMode requires Governance.EnableMcpSecurity=true. " +
                    "The scan StrictDriftMode tunes only runs when MCP security scanning is enabled — " +
                    "with it off, a drifted tool definition is silently admitted regardless of this setting.");
        });

        // Exemptions are checked whether or not the posture is on, because a malformed entry is a typo
        // in either case and the list is read by operators long before it is read by the governor.
        RuleForEach(x => x.ToolBehaviorGating.Exemptions)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Tool))
            .WithMessage(
                "ToolBehaviorGating.Exemptions contains an entry with no tool name. A blank name " +
                "matches nothing and exempts nothing — remove the entry or name the tool.");

        RuleForEach(x => x.ToolBehaviorGating.Exemptions)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Reason))
            .WithMessage(
                "ToolBehaviorGating.Exemptions contains an entry with no reason. Every exemption must " +
                "say why the tool is safe despite not declaring itself read-only: this list is the " +
                "first thing a reviewer reads when asking why a tool was never gated, and an entry " +
                "with no justification is indistinguishable from one added to silence a prompt.");
    }
}
