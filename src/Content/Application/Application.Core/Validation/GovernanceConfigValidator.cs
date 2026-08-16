using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
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

        // #386: EnablePromptInjectionDetection and EnableMcpSecurity used to require Enabled=true,
        // because the composition root only ever wired the AGT kernel path when Enabled was true —
        // turning one on while governance was "disabled" was a silent no-op. That coupling is gone:
        // the composition root (Presentation.Common.IServiceCollectionExtensions,
        // Infrastructure.AI.MCPServer.Program) now stands up AddGovernanceDependencies whenever ANY
        // of Enabled, EnablePromptInjectionDetection, or EnableMcpSecurity is true, and
        // AddGovernanceDependencies itself only loads the declarative policy layer (PolicyPaths,
        // IGovernancePolicyEngine) when Enabled is true — resolving NoOpPolicyEngine otherwise. So
        // Enabled now governs the policy layer alone; the other two arm the kernel on their own and
        // no longer need this validator's help to do so.

        // The same landmine shape, for the behaviour posture — but stated precisely, because the imprecise
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

        // Same landmine as ToolBehaviorGating above, for the tool-composition posture: it is applied by
        // the same tool governor, so it needs the same company. See that rule's message for why leaving
        // enforcement off is not "posture off" but "posture applies to bundle runs alone".
        When(x => x.ToolCompositionGating.DefaultPosture == CompositionPosture.RequireApproval
                  || x.ToolCompositionGating.Pairings.Any(p => p.Posture == CompositionPosture.RequireApproval), () =>
        {
            RuleFor(x => x.EnforceToolInvocation)
                .Equal(true)
                .WithMessage(
                    "ToolCompositionGating has a pairing (or DefaultPosture) set to RequireApproval, " +
                    "which requires Governance.EnforceToolInvocation=true. The posture is applied by " +
                    "the same tool governor ToolBehaviorGating uses, which engages either when " +
                    "invocation enforcement is on or when a bundle run publishes a capability envelope " +
                    "— so leaving enforcement off does not switch the posture off, it applies it to " +
                    "bundle runs alone while every agent turn and plan step goes ungated.");
        });

        RuleForEach(x => x.ToolCompositionGating.Pairings)
            .Must(p => ToolCompositionCapabilities.SourceBits.Contains(p.Source))
            .WithMessage(
                "ToolCompositionGating.Pairings contains an entry whose Source is not a source " +
                "capability (IngestsUntrustedInput or ReadsCredentials). A pairing names one source " +
                "bit and one sink bit — remove the entry or correct the capability.");

        RuleForEach(x => x.ToolCompositionGating.Pairings)
            .Must(p => ToolCompositionCapabilities.SinkBits.Contains(p.Sink))
            .WithMessage(
                "ToolCompositionGating.Pairings contains an entry whose Sink is not a sink capability " +
                "(WritesFiles, ExecutesCode, or SendsOutbound). A pairing names one source bit and one " +
                "sink bit — remove the entry or correct the capability.");

        RuleFor(x => x.ToolCompositionGating.Pairings)
            .Must(pairings => pairings.Select(p => (p.Source, p.Sink)).Distinct().Count() == pairings.Count)
            .WithMessage(
                "ToolCompositionGating.Pairings contains two entries for the same (Source, Sink) pair. " +
                "Only the first is ever consulted — remove the duplicate or the operator's later " +
                "posture change will silently do nothing.");

        // Per-tool overrides: name and reason are the same "first thing a reviewer reads" requirement
        // as ToolBehaviorGating.Exemptions above, for the identical reason.
        RuleForEach(x => x.ToolCompositionGating.ToolCapabilities)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Tool))
            .WithMessage(
                "ToolCompositionGating.ToolCapabilities contains an entry with no tool name. A blank " +
                "name matches nothing and overrides nothing — remove the entry or name the tool.");

        RuleForEach(x => x.ToolCompositionGating.ToolCapabilities)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Reason))
            .WithMessage(
                "ToolCompositionGating.ToolCapabilities contains an entry with no reason. Every " +
                "override must say why — this list is the first thing a reviewer reads when asking why " +
                "a tool was, or was not, flagged.");

        // The loosening-direction rule ToolBehaviorExemption.Server already enforces, restated here for
        // the same reason: clearing a name-keyed tool's capabilities without naming its server hands
        // back the exact bypass that rule exists to prevent.
        RuleForEach(x => x.ToolCompositionGating.ToolCapabilities)
            .Must(entry => entry.Capabilities.Count > 0 || !string.IsNullOrWhiteSpace(entry.Server))
            .WithMessage(
                "ToolCompositionGating.ToolCapabilities contains an entry with an empty Capabilities " +
                "list and no Server. Clearing a tool's capabilities by name alone would apply to any " +
                "server that ever advertises a tool by that name — name the server this override is " +
                "actually for.");

        RuleForEach(x => x.ToolCompositionGating.ServerCapabilities)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Server))
            .WithMessage(
                "ToolCompositionGating.ServerCapabilities contains an entry with no server name. A " +
                "blank name matches nothing and overrides nothing — remove the entry or name the server.");

        RuleForEach(x => x.ToolCompositionGating.ServerCapabilities)
            .Must(entry => !string.IsNullOrWhiteSpace(entry.Reason))
            .WithMessage(
                "ToolCompositionGating.ServerCapabilities contains an entry with no reason. Every " +
                "override must say why every tool on that server carries these capabilities.");

        // A server override may only ADD capability bits — see ToolCompositionGatingConfig's remarks
        // for why clearing is restricted to a named, per-tool override. An empty list here is either a
        // typo or an attempt to de-taint a whole server by omission, and neither is honoured silently.
        RuleForEach(x => x.ToolCompositionGating.ServerCapabilities)
            .Must(entry => entry.Capabilities.Count > 0)
            .WithMessage(
                "ToolCompositionGating.ServerCapabilities contains an entry with an empty Capabilities " +
                "list. A server override may only ADD capability bits to every tool it advertises — an " +
                "empty list adds nothing and the entry does nothing; remove it or list the capabilities.");
    }
}
