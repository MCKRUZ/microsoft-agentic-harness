using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Application.Common.Helpers;
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
        var governance = _governanceConfig.CurrentValue;
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
        if (approvalReasons is not null)
        {
            var gate = await RequestApprovalAsync(agentId, toolName,
                string.Join("; ", approvalReasons), profile, arguments, cancellationToken)
                .ConfigureAwait(false);

            if (gate.Block is { } block)
                return block;

            approvedBy = gate.Reason;
        }

        Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
            approvedBy is null ? "allowed" : $"approved by human: {approvedBy}",
            profile.Radius,
            RequiredApproval: approvedBy is not null,
            ApprovalGranted: approvedBy is not null,
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

    private void Record(ToolDecisionRecord record)
    {
        lock (_lock)
            _decisions.Add(record);
    }

    /// <inheritdoc />
    public void RecordDownstreamBlock(string toolName, string reason)
    {
        // Only meaningful on an enforced turn. Off that path the governor recorded nothing, so there
        // is no allow to correct and nothing to add.
        //
        // EnforcementActive is consulted as well as _enforcedObserved, and the difference is
        // load-bearing: the authorization gate runs BEFORE this governor, so on the first tool call
        // of a turn nothing has set _enforcedObserved yet. Keying only on that flag silently dropped
        // every RBAC refusal that arrived first — which is most of them, since a refused call never
        // goes on to reach the governor at all. This matches how GetTrace already decides whether a
        // turn was enforced.
        if (!_enforcedObserved && !EnforcementActive)
            return;

        var radius = _toolRiskClassifier.Classify(toolName).Radius;
        Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Denied, reason, radius,
            RequiredApproval: false, ApprovalGranted: false, Enforced: true));

        // Deliberately no audit write. This method corrects the trace on behalf of a gate that has
        // already audited its own refusal in its own vocabulary; writing a second line here would make
        // every downstream block count twice for anyone tallying denials from the audit stream. The
        // caller audits because it always can — this method is inert off the enforced path, and a host
        // may register observers with governance enforcement switched off.
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
