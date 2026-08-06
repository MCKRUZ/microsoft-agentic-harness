using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI;
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
/// <strong>An approval answers the permission layer and nothing else.</strong> A human who approves
/// an <c>Ask</c> does not thereby clear the capability envelope, sandbox capability enforcement, or
/// the declarative policy engine — the call resumes into those gates exactly where an outright
/// <c>Allow</c> would have entered them, and any one of them can still refuse it. This is worth
/// stating because getting it wrong is silent: an earlier revision returned an allow straight from
/// the approval, so an approver could have granted a tool the sandbox never granted the capability
/// for. Approval advances a call to the checks that were already going to run; it never carries it
/// past them.
/// </para>
/// </remarks>
public sealed partial class ToolInvocationGovernor : IToolInvocationGovernor
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolPermissionService _toolPermissionService;
    private readonly IToolRiskClassifier _toolRiskClassifier;
    private readonly IAutonomyDecisionEvaluator _autonomyEvaluator;
    private readonly IGovernancePolicyEngine _policyEngine;
    private readonly IGovernanceAuditService _auditService;
    private readonly IDenialTracker _denialTracker;
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IToolApprovalRouter _approvalRouter;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ILogger<ToolInvocationGovernor> _logger;

    private readonly object _lock = new();
    private readonly List<ToolDecisionRecord> _decisions = [];

    // Set true the first time a tool is authorized under active enforcement this turn, so GetTrace reports
    // the turn as enforced even if it is called after the bundle run's ambient scope has torn down.
    private bool _enforcedObserved;

    public ToolInvocationGovernor(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IToolRiskClassifier toolRiskClassifier,
        IAutonomyDecisionEvaluator autonomyEvaluator,
        IGovernancePolicyEngine policyEngine,
        IGovernanceAuditService auditService,
        IDenialTracker denialTracker,
        ICapabilityEnforcer capabilityEnforcer,
        IToolApprovalRouter approvalRouter,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IOptionsMonitor<PermissionsConfig> permissionsConfig,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ILogger<ToolInvocationGovernor> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _toolRiskClassifier = toolRiskClassifier;
        _autonomyEvaluator = autonomyEvaluator;
        _policyEngine = policyEngine;
        _auditService = auditService;
        _denialTracker = denialTracker;
        _capabilityEnforcer = capabilityEnforcer;
        _approvalRouter = approvalRouter;
        _governanceConfig = governanceConfig;
        _permissionsConfig = permissionsConfig;
        _sandboxConfig = sandboxConfig;
        _logger = logger;
    }

    /// <summary>
    /// Whether the current flow is a bundle run — i.e. a per-caller <see cref="CapabilityEnvelope"/> has been
    /// published for it. A bundle executes an externally-authored agent, so its whole flow must be governed
    /// and fail closed; this is the single ambient fact the enforcement decision derives from, so there is no
    /// way to publish an envelope without also arming the governor.
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

    /// <summary>
    /// Whether per-invocation enforcement is active for the current flow. True when the host has opted in
    /// globally (<c>GovernanceConfig.EnforceToolInvocation</c>) <em>or</em> a bundle run is active
    /// (<see cref="BundleRunActive"/>) — bundle runs must always be governed so the per-caller capability
    /// envelope is never inert. Off both paths this is false and the governor is a pure pass-through,
    /// unchanged for existing deployments.
    /// </summary>
    private bool EnforcementActive =>
        _governanceConfig.CurrentValue.EnforceToolInvocation || BundleRunActive;

    /// <inheritdoc />
    public async ValueTask<ToolInvocationDecision> AuthorizeAsync(
        string toolName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        // Opt-in: when enforcement is off the governor never engages — pure pass-through, no record,
        // no behaviour change for existing deployments.
        if (!EnforcementActive)
            return ToolInvocationDecision.Allow();

        // Enforcement ran this turn; remember it so the trace reports the turn as governed even if GetTrace
        // is called after a bundle run's ambient envelope scope has already disposed.
        _enforcedObserved = true;

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
                Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Denied,
                    "no agent identity in a bundle run", profile.Radius,
                    RequiredApproval: false, ApprovalGranted: false, Enforced: true));
                return ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(toolName));
            }

            _logger.LogWarning(
                "Tool governance: no AgentId in execution context for {ToolName} — allowed ungoverned and recorded", toolName);
            Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
                "no agent identity in execution context", profile.Radius,
                RequiredApproval: false, ApprovalGranted: false, Enforced: false));
            return ToolInvocationDecision.Allow();
        }

        var permission = await _toolPermissionService
            .ResolvePermissionAsync(agentId, toolName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Graded-autonomy risk gate: can only tighten an Allow, never loosen.
        permission = ApplyRiskGate(permission, toolName, profile);

        switch (permission.Behavior)
        {
            case PermissionBehaviorType.Deny:
                _denialTracker.RecordDenial(agentId, toolName);
                return Blocked(toolName, ToolDecisionOutcome.Denied, permission.Reason, profile.Radius,
                    requiredApproval: false, agentId);

            case PermissionBehaviorType.Ask:
                // A human answers the PERMISSION layer only. Approval does not skip the gates that
                // have not ruled yet — it advances the call into them, exactly where an outright
                // Allow would have entered.
                var gate = await RequestApprovalAsync(agentId, toolName, permission.Reason, profile,
                    arguments, cancellationToken).ConfigureAwait(false);

                if (!gate.Granted)
                    return gate.Block!;

                return await AuthorizeGrantedAsync(agentId, toolName, profile, arguments,
                    approval: gate, cancellationToken).ConfigureAwait(false);

            case PermissionBehaviorType.Allow:
                return await AuthorizeGrantedAsync(agentId, toolName, profile, arguments,
                    approval: null, cancellationToken).ConfigureAwait(false);

            default:
                // Unknown behaviour: fail closed.
                return Blocked(toolName, ToolDecisionOutcome.Denied,
                    $"unrecognized permission behaviour '{permission.Behavior}'", profile.Radius,
                    requiredApproval: false, agentId);
        }
    }

    /// <summary>
    /// Runs every gate after the permission layer for a tool the rule layer has cleared — the armed
    /// capability envelope, sandbox capability enforcement, and the declarative policy engine — and
    /// allows only if all of them agree.
    /// </summary>
    /// <param name="approval">
    /// The human approval that got the call here, when it arrived via an <c>Ask</c> verdict; null
    /// when the permission layer allowed it outright.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Both entry paths converge here deliberately.</strong> An approved <c>Ask</c> used to
    /// return an allow directly, which silently skipped all three of these gates — so an approver
    /// could grant a tool the sandbox had not granted the capability for, or one an armed envelope
    /// did not list. A human answers the permission question; they do not answer the capability,
    /// envelope, or policy questions, and must not be able to.
    /// </para>
    /// <para>
    /// A prior approval <em>does</em> satisfy a subsequent policy <c>RequireApproval</c> for the
    /// same call: it is the same human ruling on the same invocation with the same arguments, so
    /// asking twice would be a worse experience with no additional safety.
    /// </para>
    /// </remarks>
    private async ValueTask<ToolInvocationDecision> AuthorizeGrantedAsync(
        string agentId, string toolName, ToolRiskProfile profile,
        IReadOnlyDictionary<string, object?>? arguments, ApprovalGate? approval,
        CancellationToken cancellationToken)
    {
        // Independent envelope confirmation. Defence in depth against a resolver arbitration bug —
        // see EnvelopeGrantsToolWhenArmed's remarks for the two occasions that arbitration was wrong.
        if (!EnvelopeGrantsToolWhenArmed(toolName))
        {
            _denialTracker.RecordDenial(agentId, toolName);
            return Blocked(toolName, ToolDecisionOutcome.Denied,
                GovernanceDenials.NotPermitted(toolName), profile.Radius,
                requiredApproval: approval is not null, agentId);
        }

        // Capability enforcement: the rule layer allowed the tool, now confirm the granted sandbox
        // capabilities satisfy what the tool needs.
        var grantedCapabilities = ToolCapability.None;
        foreach (var name in _sandboxConfig.CurrentValue.DefaultGrantedCapabilities)
            if (Enum.TryParse<ToolCapability>(name, ignoreCase: true, out var cap))
                grantedCapabilities |= cap;

        var capResult = await _capabilityEnforcer
            .EnforceAsync(toolName, grantedCapabilities, ct: cancellationToken)
            .ConfigureAwait(false);

        if (!capResult.IsSuccess)
        {
            var reason = capResult.Errors.Count > 0 ? capResult.Errors[0] : "capability check failed";
            return Blocked(toolName, ToolDecisionOutcome.Denied, $"capability violation: {reason}",
                profile.Radius, requiredApproval: approval is not null, agentId);
        }

        // Declarative policy layer (YAML policies), only when configured.
        var governance = _governanceConfig.CurrentValue;
        if (governance.Enabled && _policyEngine.HasPolicies)
        {
            var decision = _policyEngine.EvaluateToolCall(agentId, toolName);

            // The outcome is audited once below — by Blocked() on a deny/approval, or by the final
            // Allowed audit on success — so the policy action is not logged separately here.
            if (!decision.IsAllowed)
            {
                if (decision.Action == GovernancePolicyAction.RequireApproval)
                {
                    // Already approved for this same call — do not ask the same human twice.
                    if (approval is null)
                    {
                        var gate = await RequestApprovalAsync(agentId, toolName, decision.Reason, profile,
                            arguments, cancellationToken).ConfigureAwait(false);

                        if (!gate.Granted)
                            return gate.Block!;

                        approval = gate;
                    }
                }
                else
                {
                    _denialTracker.RecordDenial(agentId, toolName);
                    return Blocked(toolName, ToolDecisionOutcome.Denied, decision.Reason, profile.Radius,
                        requiredApproval: approval is not null, agentId);
                }
            }
        }

        Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
            approval is { } granted ? $"approved by human: {granted.Reason}" : "allowed",
            profile.Radius,
            RequiredApproval: approval is not null,
            ApprovalGranted: approval is not null,
            Enforced: true));

        if (governance.EnableAudit)
            _auditService.Log(agentId, toolName, ToolDecisionOutcome.Allowed.ToString());

        return ToolInvocationDecision.Allow();
    }

    /// <summary>
    /// Records a blocking decision (audit + trace) and returns a deny carrying a model-facing message.
    /// </summary>
    private ToolInvocationDecision Blocked(
        string toolName, ToolDecisionOutcome outcome, string reason, BlastRadius radius,
        bool requiredApproval, string agentId)
    {
        Record(new ToolDecisionRecord(toolName, outcome, reason, radius,
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

        if (!Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out var tier))
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

    private void Record(ToolDecisionRecord record)
    {
        lock (_lock)
            _decisions.Add(record);
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            _decisions.Clear();
            _enforcedObserved = false;
        }
    }

    /// <inheritdoc />
    public GovernanceTrace GetTrace()
    {
        // Prefer the snapshot taken while authorizing: a bundle run's ambient enforcement signal may have
        // torn down by the time the trace is assembled, but a turn that authorized under enforcement is
        // still an enforced turn.
        var enforced = _enforcedObserved || EnforcementActive;
        lock (_lock)
        {
            if (!enforced && _decisions.Count == 0)
                return GovernanceTrace.Empty;

            return new GovernanceTrace
            {
                EnforcementEnabled = enforced,
                ToolDecisions = _decisions.ToList()
            };
        }
    }
}
