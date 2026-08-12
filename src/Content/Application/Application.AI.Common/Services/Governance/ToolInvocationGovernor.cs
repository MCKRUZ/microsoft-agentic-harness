using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Domain.Common.Helpers;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Scoped governor that authorizes the agent's autonomous tool calls on the live tool path,
/// applying the same permission, graded-autonomy risk, capability, and policy logic the MediatR
/// behaviours define — which never executed for agent tool calls because nothing produces
/// <c>IToolRequest</c>. Records every decision into a per-turn <see cref="GovernanceTrace"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in.</strong> Off unless <c>GovernanceConfig.EnforceToolInvocation</c> is true. When off,
/// <see cref="AuthorizeAsync"/> is a pure pass-through (no evaluation, no recording) so default
/// deployments are unchanged. When on, the gate is <em>fail-closed</em>.
/// </para>
/// <para>
/// <strong>Approval handling.</strong> A tool that resolves to "requires approval" is routed to a
/// human through <see cref="IToolApprovalRouter"/>: the call is suspended, an escalation naming the
/// tool and its arguments is raised, and the call proceeds only on an affirmative decision. When
/// routing is switched off — the default — or when it cannot run, the verdict degrades to the
/// original behaviour: recorded as <see cref="ToolDecisionOutcome.PendingApproval"/> and blocked,
/// fail-closed.
/// </para>
/// <para>
/// <strong>An approval never substitutes for a deterministic check.</strong> A human who approves an
/// <c>Ask</c> does not thereby clear the capability envelope, sandbox capability enforcement, or the
/// declarative policy engine. That property is now guaranteed by <em>ordering</em>: every gate that
/// can decide the call without a human runs first, and the human is asked only once nothing
/// automatic objects — so an approval can only ever clear the one question actually put to it. See
/// <see cref="AuthorizeInOrderAsync"/> for why the reasons are accumulated and asked as one
/// question. This is worth stating because getting it wrong is silent, and it has been wrong twice:
/// once by returning an allow straight from the approval (an approver could have granted a tool the
/// sandbox never granted the capability for), and once by letting an approval obtained for one
/// gate's reason satisfy a later gate whose reason the approver was never shown.
/// </para>
/// <para>
/// <strong>Stateless.</strong> Every decision is written to the turn-scoped
/// <see cref="IGovernanceTraceRecorder"/> and nothing is kept here. That is deliberate: accumulating
/// the audit trail was this type's second job and the only reason it held mutable state, and the two
/// jobs have different consumers — this one only writes, while diagnostics, reporting and tests only
/// read. Reading the trail therefore no longer requires constructing a governor.
/// </para>
/// </remarks>
public sealed partial class ToolInvocationGovernor : IToolInvocationGovernor
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolPermissionService _toolPermissionService;
    private readonly IToolRiskClassifier _toolRiskClassifier;
    private readonly IToolBehaviorRegistry _toolBehaviorRegistry;
    private readonly IAutonomyDecisionEvaluator _autonomyEvaluator;
    private readonly IGovernancePolicyEngine _policyEngine;
    private readonly IGovernanceAuditService _auditService;
    private readonly IDenialTracker _denialTracker;
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IToolApprovalRouter _approvalRouter;
    private readonly IGovernanceTraceRecorder _trace;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ILogger<ToolInvocationGovernor> _logger;

    public ToolInvocationGovernor(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IToolRiskClassifier toolRiskClassifier,
        IToolBehaviorRegistry toolBehaviorRegistry,
        IAutonomyDecisionEvaluator autonomyEvaluator,
        IGovernancePolicyEngine policyEngine,
        IGovernanceAuditService auditService,
        IDenialTracker denialTracker,
        ICapabilityEnforcer capabilityEnforcer,
        IToolApprovalRouter approvalRouter,
        IGovernanceTraceRecorder trace,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IOptionsMonitor<PermissionsConfig> permissionsConfig,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ILogger<ToolInvocationGovernor> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _toolRiskClassifier = toolRiskClassifier;
        _toolBehaviorRegistry = toolBehaviorRegistry;
        _autonomyEvaluator = autonomyEvaluator;
        _policyEngine = policyEngine;
        _auditService = auditService;
        _denialTracker = denialTracker;
        _capabilityEnforcer = capabilityEnforcer;
        _approvalRouter = approvalRouter;
        _trace = trace;
        _governanceConfig = governanceConfig;
        _permissionsConfig = permissionsConfig;
        _sandboxConfig = sandboxConfig;
        _logger = logger;
    }

    /// <summary>
    /// Whether the current flow is a bundle run — i.e. a per-caller <see cref="CapabilityEnvelope"/> has been
    /// published for it. Read here only to decide how a <em>missing agent identity</em> is treated, which is
    /// stricter inside a bundle than outside one. It is a narrower question than "should this flow be
    /// governed", which is <see cref="GovernanceEnforcement.IsActive"/>'s and is answered there alone.
    /// </summary>
    private static bool BundleRunActive => CapabilityEnvelopeAccessor.Current is not null;

    /// <summary>
    /// Independently confirms that an ambient capability envelope grants <paramref name="toolName"/>,
    /// after the permission resolver has already said Allow. Returns true when no envelope is armed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is defence in depth, and until now there was none.</strong> The permission resolver
    /// was the sole enforcement point for tool names at invocation: when its arbitration was wrong, the
    /// caller got the tool. That arbitration has been wrong twice — a tier default that allowed anything
    /// unmatched, then a plugin baseline that outranked the envelope's closing deny — and both were
    /// found by review rather than by the system refusing.
    /// </para>
    /// <para>
    /// <c>CapabilityEnvelope.GrantsTool</c> existed for exactly this check and had zero callers, which is
    /// why neither defect was caught at the point of use. This wires it in: a resolver Allow for a tool
    /// the armed envelope does not list is refused and recorded as a denial, so a future arbitration bug
    /// costs a blocked call rather than an unauthorized one.
    /// </para>
    /// <para>
    /// The two must agree by construction — the envelope's own rules are built from the same
    /// <c>AllowedTools</c> list this reads, matched with the same case-insensitive comparer. A
    /// disagreement therefore means the resolver reached Allow by a path that did not consult the
    /// envelope, which is precisely the condition worth failing closed on.
    /// </para>
    /// </remarks>
    /// <param name="toolName">The tool the resolver has authorized.</param>
    /// <returns>True when no envelope is armed, or when the armed envelope grants the tool.</returns>
    private static bool EnvelopeGrantsToolWhenArmed(string toolName)
        => CapabilityEnvelopeAccessor.Current is not { } envelope || envelope.GrantsTool(toolName);

    /// <inheritdoc />
    public async ValueTask<ToolInvocationDecision> AuthorizeAsync(
        string toolName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        // Opt-in: when enforcement is off the governor never engages — pure pass-through, no record,
        // no behaviour change for existing deployments. Read live rather than from the trace's sticky
        // form, so a bundle run stops being enforced the moment its envelope scope disposes.
        if (!GovernanceEnforcement.IsActive(_governanceConfig.CurrentValue))
            return ToolInvocationDecision.Allow();

        // Enforcement ran this turn; remember it so the trace reports the turn as governed even if it
        // is assembled after a bundle run's ambient envelope scope has already disposed.
        _trace.MarkEnforced();

        var profile = _toolRiskClassifier.Classify(toolName);

        // No agent identity means this isn't a fully-scoped agent turn (e.g. an execution path that did not
        // initialize the context). Off the bundle path we pass through but RECORD it ungoverned so the trace
        // surfaces the gap. Inside a bundle run we must NEVER allow ungoverned — an ephemeral agent whose
        // identity is missing is exactly the shape a bypass would take, so fail closed.
        var agentId = _executionContext.AgentId;
        if (string.IsNullOrEmpty(agentId))
        {
            if (BundleRunActive)
            {
                _logger.LogWarning(
                    "Tool governance: no AgentId in execution context for {ToolName} during a bundle run — denied (fail-closed)",
                    toolName);
                _trace.Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Denied,
                    "no agent identity in a bundle run", profile.Radius,
                    RequiredApproval: false, ApprovalGranted: false, Enforced: true));
                return ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(toolName));
            }

            _logger.LogWarning(
                "Tool governance: no AgentId in execution context for {ToolName} — allowed ungoverned and recorded", toolName);
            _trace.Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
                "no agent identity in execution context", profile.Radius,
                RequiredApproval: false, ApprovalGranted: false, Enforced: false));
            return ToolInvocationDecision.Allow();
        }

        var permission = await _toolPermissionService
            .ResolvePermissionAsync(agentId, toolName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Graded-autonomy risk gate: can only tighten an Allow, never loosen.
        permission = ApplyRiskGate(permission, toolName, profile);

        // Every gate that can decide on its own runs first; the human is asked last, once.
        // See AuthorizeInOrderAsync for why that ordering is the design and not an accident.
        return await AuthorizeInOrderAsync(agentId, toolName, permission, profile, arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the gates in decision order — permission, capability envelope, sandbox capabilities,
    /// declarative policy — and only then, if anything still wants a human, asks one. Once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deterministic gates first, the human last.</strong> Every check before the approval is
    /// cheap, in-process, and argument-independent: it will reach the same verdict whether or not a
    /// human is consulted. Asking first — the shape this started as — meant approvers could be paged
    /// for a call the capability enforcer was always going to refuse, with the agent's turn stalled
    /// for the whole approval timeout to reach a denial that was knowable up front. It also risks
    /// training approvers that their answer does not matter.
    /// </para>
    /// <para>
    /// <strong>One question, carrying every reason.</strong> Both the permission layer's <c>Ask</c>
    /// and the policy engine's <c>RequireApproval</c> can demand a human, and they demand it for
    /// <em>different</em> reasons written by different authors. Asking twice is a poor experience;
    /// asking once and showing only the first reason is worse than that — it means a human approves
    /// "write tools need sign-off" and thereby silently clears "production schema changes need DBA
    /// review", a question they were never shown. Accumulating the reasons and asking once shows the
    /// approver everything that is actually being decided.
    /// </para>
    /// <para>
    /// <strong>Approval last also keeps the audit trail honest.</strong> Because nothing can refuse
    /// the call after a human approves it, a recorded block can never carry
    /// <c>RequiredApproval: true, ApprovalGranted: false</c> for a call the approver actually
    /// approved — a combination an auditor would reasonably read as "the approver said no".
    /// </para>
    /// </remarks>
    private async ValueTask<ToolInvocationDecision> AuthorizeInOrderAsync(
        string agentId, string toolName, PermissionDecision permission, ToolRiskProfile profile,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        // One snapshot for the whole decision. Two stages read this config, and reading the monitor
        // twice would let a reload land between them — deciding half the call under one policy and
        // half under another, which is not a state any operator asked for.
        var governance = _governanceConfig.CurrentValue;

        // Accumulates every reason a human must rule on this call, from whichever gate raised it.
        // Left null until a gate actually asks for one: the overwhelmingly common path is a call no
        // gate wants a human for, and this runs on every authorized tool call.
        List<string>? approvalReasons = null;

        switch (permission.Behavior)
        {
            case PermissionBehaviorType.Deny:
                _denialTracker.RecordDenial(agentId, toolName);
                return Blocked(toolName, ToolDecisionOutcome.Denied, permission.Reason, profile.Radius,
                    requiredApproval: false, agentId);

            case PermissionBehaviorType.Ask:
                (approvalReasons ??= []).Add(permission.Reason);
                break;

            case PermissionBehaviorType.Allow:
                break;

            default:
                // Unknown behaviour: fail closed.
                return Blocked(toolName, ToolDecisionOutcome.Denied,
                    $"unrecognized permission behaviour '{permission.Behavior}'", profile.Radius,
                    requiredApproval: false, agentId);
        }

        // Behaviour posture: what the tool declared it does, rather than whether anyone listed it.
        // Deliberately a third source of approval reasons rather than a sixth admission gate — a gate
        // of its own would need its own route to a human, and two independent approval questions about
        // one call is exactly the shape this method exists to prevent.
        if (RequiresApprovalForDeclaredBehavior(toolName, governance.ToolBehaviorGating, out var behaviorReason))
            (approvalReasons ??= []).Add(behaviorReason);

        // Independent envelope confirmation. Defence in depth against a resolver arbitration bug —
        // see EnvelopeGrantsToolWhenArmed's remarks for the two occasions that arbitration was wrong.
        if (!EnvelopeGrantsToolWhenArmed(toolName))
        {
            _denialTracker.RecordDenial(agentId, toolName);
            return Blocked(toolName, ToolDecisionOutcome.Denied,
                GovernanceDenials.NotPermitted(toolName), profile.Radius,
                requiredApproval: false, agentId);
        }

        // Capability enforcement: the rule layer cleared the tool, now confirm the granted sandbox
        // capabilities satisfy what the tool needs. Parsing goes through the shared reader rather
        // than a local loop — this is a GRANT list, so the name-only rule it enforces is what stops
        // a numeric entry setting every bit and making the check below unfailable, and two copies of
        // that rule is exactly how one of them ends up not having it.
        var grantedCapabilities = ToolPermissionProfileResolver.ParseCapabilities(
            _sandboxConfig.CurrentValue.DefaultGrantedCapabilities);

        var capResult = await _capabilityEnforcer
            .EnforceAsync(toolName, grantedCapabilities, ct: cancellationToken)
            .ConfigureAwait(false);

        if (!capResult.IsSuccess)
        {
            var reason = capResult.Errors.Count > 0 ? capResult.Errors[0] : "capability check failed";
            return Blocked(toolName, ToolDecisionOutcome.Denied, $"capability violation: {reason}",
                profile.Radius, requiredApproval: false, agentId);
        }

        // Declarative policy layer (YAML policies), only when configured.
        if (governance.Enabled && _policyEngine.HasPolicies)
        {
            // Arguments are forwarded. The policy engine builds its rule-evaluation context from them,
            // so a rule conditioned on an argument value ("deny sql_query where database == 'prod'")
            // can only ever match when they are supplied. Passing the tool name alone did not make
            // such rules deny-by-default — it made them unmatchable, so an operator's rule was loaded,
            // reported as active, and silently never fired on the live tool path.
            var decision = _policyEngine.EvaluateToolCall(agentId, toolName, arguments);

            // The outcome is audited once below — by Blocked() on a deny, or by the final Allowed
            // audit on success — so the policy action is not logged separately here.
            if (!decision.IsAllowed)
            {
                if (decision.Action == GovernancePolicyAction.RequireApproval)
                {
                    (approvalReasons ??= []).Add(decision.Reason);
                }
                else
                {
                    _denialTracker.RecordDenial(agentId, toolName);
                    return Blocked(toolName, ToolDecisionOutcome.Denied, decision.Reason, profile.Radius,
                        requiredApproval: false, agentId);
                }
            }
        }

        // Nothing deterministic refuses this call. If any gate wants a human, this is the moment.
        string? approvedBy = null;
        ApprovedCall? approvedCall = null;
        if (approvalReasons is not null)
        {
            var gate = await RequestApprovalAsync(agentId, toolName,
                string.Join("; ", approvalReasons), profile, arguments, cancellationToken)
                .ConfigureAwait(false);

            if (gate.Block is { } block)
                return block;

            approvedBy = gate.Reason;
            approvedCall = gate.Call;
        }

        _trace.Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
            approvedBy is null ? "allowed" : $"approved by human: {approvedBy}",
            profile.Radius,
            RequiredApproval: approvedBy is not null,
            ApprovalGranted: approvedBy is not null,
            Enforced: true));

        if (governance.EnableAudit)
            _auditService.Log(agentId, toolName, ToolDecisionOutcome.Allowed.ToString());

        return approvedCall is { } call ? ToolInvocationDecision.Allow(call) : ToolInvocationDecision.Allow();
    }

    /// <summary>
    /// Records a blocking decision (audit + trace) and returns a deny carrying a model-facing message.
    /// </summary>
    private ToolInvocationDecision Blocked(
        string toolName, ToolDecisionOutcome outcome, string reason, BlastRadius radius,
        bool requiredApproval, string agentId)
    {
        _trace.Record(new ToolDecisionRecord(toolName, outcome, reason, radius,
            RequiredApproval: requiredApproval, ApprovalGranted: false, Enforced: true));

        if (_governanceConfig.CurrentValue.EnableAudit)
            _auditService.Log(agentId, toolName, outcome.ToString());

        _logger.LogWarning(
            "Tool governance blocked agent {AgentId} tool {ToolName}: {Outcome} — {Reason}",
            agentId, toolName, outcome, reason);

        // Model-facing message is deliberately generic: the detailed reason (rule ids, paths,
        // capability internals) stays in the structured log and the GovernanceTrace, never relayed
        // to the LLM — avoids leaking operator-authored policy detail into model-visible content.
        return ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(toolName));
    }

    /// <summary>
    /// Whether the non-read-only approval posture wants a human for this call, and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The default is inverted here and nowhere else.</strong> Every other check in this class
    /// asks whether something forbids the call; this one asks whether anything permits it, and treats
    /// silence as "no". That is the whole point: a name list can only refuse tools somebody thought of,
    /// so a tool arriving at runtime from a server nobody on the team wrote is callable until it is
    /// noticed. Reading the tool's own declaration means a new mutating tool is gated the moment it
    /// appears, with no list to edit.
    /// </para>
    /// <para>
    /// <strong>Off costs nothing.</strong> The posture is read first and returns immediately when
    /// disabled, before the registry is consulted, so a host that has not opted in pays one boolean
    /// read per tool call.
    /// </para>
    /// <para>
    /// <strong>An exemption is honoured even for a self-declared destructive tool.</strong> That looks
    /// wrong beside the rule that a destructive claim outranks a read-only one, and is a different
    /// question: that rule arbitrates between two claims by the <em>same</em> party, while an exemption
    /// is the operator overruling the party outright, in writing, with a reason. Silently ignoring some
    /// entries in a list an operator maintains is how a control becomes untrustworthy.
    /// </para>
    /// </remarks>
    /// <param name="toolName">The tool being authorized.</param>
    /// <param name="gating">The posture, from the same config snapshot the rest of the decision uses.</param>
    /// <param name="reason">The approver-facing reason, when one is needed.</param>
    /// <returns>True when a human must rule on this call because of what the tool declared.</returns>
    private bool RequiresApprovalForDeclaredBehavior(
        string toolName, ToolBehaviorGatingConfig gating, out string reason)
    {
        reason = string.Empty;

        if (!gating.RequireApprovalForNonReadOnlyTools)
            return false;

        var behavior = _toolBehaviorRegistry.Resolve(toolName);
        if (behavior.NonExemptReason is not { } nonExempt)
            return false;

        var exemption = gating.Exemptions.FirstOrDefault(
            entry => string.Equals(entry.Tool, toolName, StringComparison.OrdinalIgnoreCase)
                     && ExemptionCoversSource(entry, behavior));

        if (exemption is not null)
        {
            _logger.LogDebug(
                "Tool behaviour posture: '{ToolName}' is exempt by configuration — {ExemptionReason} "
                + "(it would otherwise be gated because {NonExemptReason})",
                toolName, exemption.Reason, nonExempt);
            return false;
        }

        reason = $"declared behaviour: {nonExempt}";
        return true;
    }

    /// <summary>
    /// Whether an exemption written for a tool name may be applied to <em>this</em> declaration of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tool name belongs to nobody. An operator exempts a name after checking one vendor's tool, and
    /// any other configured server can advertise a tool by that name tomorrow. The behaviour registry
    /// already refuses to let a shadowing server loosen a record it did not create; without this check
    /// the exemption would hand that bypass straight back, because it matched on the name the attacker
    /// chose.
    /// </para>
    /// <para>
    /// So a bare name is accepted only for a declaration from somewhere the operator has already
    /// vouched for — their own code, or a server marked trusted. For anything else the exemption must
    /// name the server it was written for.
    /// </para>
    /// </remarks>
    private static bool ExemptionCoversSource(ToolBehaviorExemption exemption, ToolBehavior behavior)
        => behavior.IsVouchedFor
           || (!string.IsNullOrWhiteSpace(exemption.Server)
               && string.Equals(exemption.Server, behavior.ServerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies the graded-autonomy risk gate to an Allow decision. Tightens Allow → Ask/Deny when the
    /// active tier will not auto-approve the tool's blast radius; never loosens. This is the live home
    /// of the risk gate on the agent tool path (it formerly also existed as the now-removed
    /// <c>ToolPermissionBehavior</c>, which never fired because nothing implements <c>IToolRequest</c>).
    /// </summary>
    private PermissionDecision ApplyRiskGate(PermissionDecision decision, string toolName, ToolRiskProfile profile)
    {
        if (decision.Behavior != PermissionBehaviorType.Allow)
            return decision;

        var permissions = _permissionsConfig.CurrentValue;
        if (!permissions.GradedAutonomy.Enabled)
            return decision;

        // Name-only. A bare parse accepted "99" and ran the gate with a tier that is not a member —
        // and since the tier ordering is Restricted &lt; Supervised &lt; Autonomous, an out-of-range
        // number reads as looser than the loosest real tier. AutonomyConfigValidator applies the same
        // rule at boot, so the two agree on what a value means — but note this branch stays
        // reachable: the validator runs once at StartAsync while this reads IOptionsMonitor, so an
        // appsettings edit under reloadOnChange can invalidate the tier after boot. The branch below
        // then skips the risk gate entirely, which is fail-open and pre-dates this change.
        if (!EnumNameHelper.TryParseName<AutonomyLevel>(permissions.DefaultAutonomyLevel, out var tier))
        {
            _logger.LogWarning(
                "Graded autonomy enabled but DefaultAutonomyLevel '{Tier}' is invalid — skipping risk gate for {ToolName}",
                permissions.DefaultAutonomyLevel, toolName);
            return decision;
        }

        var result = _autonomyEvaluator.Evaluate(
            tier, profile.Radius, ChangeTargetKind.Unspecified, isStateChange: !profile.IsReadOnly, skillKey: null);

        return result.Decision switch
        {
            AutonomyDecision.AutoApprove => decision,
            AutonomyDecision.RequiresApproval => PermissionDecision.Ask(
                $"graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) requires approval under tier {tier}. {result.Reason}"),
            AutonomyDecision.Forbidden => PermissionDecision.Deny(
                $"graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) is forbidden under tier {tier}. {result.Reason}"),
            _ => decision
        };
    }
}
